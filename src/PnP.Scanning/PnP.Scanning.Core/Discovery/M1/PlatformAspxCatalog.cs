using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PnP.Scanning.Core.Discovery.M1;

/// <summary>Explicit versioned catalog evidence, frozen as exact input bytes with the Scan properties.</summary>
internal sealed class PlatformAspxCatalog
{
    private const string LayoutsPrefix = "/_layouts/";
    private const string EncodedPathSeparatorPattern = "%2f|%5c";

    private static class JsonProperties
    {
        internal const string SchemaVersion = "schemaVersion";
        internal const string CatalogVersion = "catalogVersion";
        internal const string Source = "source";
        internal const string Entries = "entries";
        internal const string Id = "id";
        internal const string DeploymentIdentity = "deploymentIdentity";
        internal const string Culture = "culture";
        internal const string Path = "path";
        internal const string Aliases = "aliases";
        internal const string DeclaredInherits = "declaredInherits";
        internal const string HandlerEvidenceSource = "handlerEvidenceSource";
    }

    internal const string Schema = "platform-aspx-catalog/v1";
    internal const int MaximumBytes = 16 * 1024 * 1024;
    internal const int MaximumTransportMessageBytes = 32 * 1024 * 1024;
    internal string Version { get; }
    internal string Source { get; }
    internal string Digest { get; }
    internal string FrozenPayload { get; }
    internal IReadOnlyList<PlatformAspxCatalogEntry> Entries { get; }

    /// <summary>Freezes validated catalog bytes, their SHA256 digest, and a read-only entry view for deterministic assessment evidence.</summary>
    private PlatformAspxCatalog(string version, string source, byte[] bytes, List<PlatformAspxCatalogEntry> entries)
    {
        Version = version;
        Source = source;
        Digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        FrozenPayload = Convert.ToBase64String(bytes);
        Entries = entries.AsReadOnly();
    }

    /// <summary>Reads a size-bounded local catalog and validates it before freezing any reference evidence.</summary>
    internal static PlatformAspxCatalog Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumBytes)
        {
            throw new ArgumentException("Catalog is missing or exceeds the supported size.", nameof(path));
        }
        return Parse(File.ReadAllBytes(path));
    }

    /// <summary>Revalidates the exact catalog bytes restored from their stored Base64 representation.</summary>
    internal static PlatformAspxCatalog FromFrozenPayload(string payload) => Parse(Convert.FromBase64String(payload));

    /// <summary>Strictly validates versioned catalog metadata, paths, aliases, and handler declarations before assigning deployment-scoped identities.</summary>
    internal static PlatformAspxCatalog Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > MaximumBytes)
        {
            throw new ArgumentException("Invalid catalog size.", nameof(bytes));
        }
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        using var document = JsonDocument.Parse(text);
        RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        if (Required(root, JsonProperties.SchemaVersion) != Schema)
        {
            throw new ArgumentException("Unsupported platform ASPX catalog schema.");
        }
        var version = Required(root, JsonProperties.CatalogVersion);
        var source = Required(root, JsonProperties.Source);
        var rows = root.GetProperty(JsonProperties.Entries);
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 65536)
        {
            throw new ArgumentException("Invalid catalog entries.");
        }
        var entries = new List<PlatformAspxCatalogEntry>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            var id = Required(row, JsonProperties.Id);
            var deployment = Required(row, JsonProperties.DeploymentIdentity);
            var culture = Required(row, JsonProperties.Culture);
            var path = NormalizePath(Required(row, JsonProperties.Path));
            var hive = Hive(path);
            var aliases = new List<string>();
            if (row.TryGetProperty(JsonProperties.Aliases, out var aliasRows))
            {
                if (aliasRows.ValueKind != JsonValueKind.Array)
                {
                    throw new ArgumentException("Catalog aliases must be an array.");
                }
                foreach (var alias in aliasRows.EnumerateArray())
                {
                    var normalized = NormalizePath(alias.GetString());
                    if (Hive(normalized) != hive)
                    {
                        throw new ArgumentException("Catalog aliases cannot collapse separate layout hives.");
                    }
                    aliases.Add(normalized);
                }
            }
            var keyMaterial = JsonSerializer.Serialize(new[] { version, deployment, culture, hive, id });
            var key = "platform:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial))).ToLowerInvariant();
            if (!identities.Add(key))
            {
                throw new ArgumentException("Duplicate platform catalog identity.");
            }
            var inherits = row.TryGetProperty(JsonProperties.DeclaredInherits, out var declared) ? declared.GetString() : null;
            var evidence = row.TryGetProperty(JsonProperties.HandlerEvidenceSource, out var evidenceValue) ? evidenceValue.GetString() : null;
            if ((inherits != null) != !string.IsNullOrWhiteSpace(evidence) || inherits != null && string.IsNullOrWhiteSpace(inherits))
                throw new ArgumentException("Catalog handler declarations require an explicit versioned evidence source.");
            entries.Add(new PlatformAspxCatalogEntry(key, id, deployment, culture, hive, path,
                aliases.Append(path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToArray(), inherits, evidence));
        }
        return new PlatformAspxCatalog(version, source, bytes, entries);
    }

    /// <summary>Returns every explicit catalog entry matching the normalized path; a path never implies an unlisted deployment.</summary>
    internal IReadOnlyList<PlatformAspxCatalogEntry> ResolvePath(string path)
    {
        var normalized = NormalizePath(path);
        return Entries.Where(entry => entry.Paths.Contains(normalized, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>Validates a query-independent layout ASPX path and normalizes its case without inventing hive aliases.</summary>
    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(LayoutsPrefix, StringComparison.OrdinalIgnoreCase) ||
            path.Contains('\\') || path.Contains('?') || path.Contains('#') || !path.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(path, EncodedPathSeparatorPattern, RegexOptions.IgnoreCase))
        {
            throw new ArgumentException("Catalog member must be a query-independent layout ASPX path.");
        }
        var decoded = Uri.UnescapeDataString(path);
        if (decoded.Contains('?') || decoded.Contains('#') || decoded.Contains('\\') ||
            decoded.Split('/').Any(part => part is "." or "..") || Regex.IsMatch(decoded, EncodedPathSeparatorPattern, RegexOptions.IgnoreCase) || decoded.Any(char.IsControl))
            throw new ArgumentException("Invalid catalog path segments.");
        var normalized = decoded.ToLowerInvariant();
        _ = Hive(normalized);
        return normalized;
    }

    /// <summary>Separates unversioned layout paths from the supported /15/ namespace and rejects invented numeric hives.</summary>
    private static string Hive(string path)
    {
        var first = path[LayoutsPrefix.Length..].Split('/')[0];
        if (first == "15")
        {
            return "hive16";
        }
        if (first.All(char.IsDigit))
        {
            throw new ArgumentException("Only /_layouts/ and /_layouts/15/ namespaces are supported; do not invent a /16/ alias.");
        }
        return "hive14";
    }

    /// <summary>Reads nonblank required catalog metadata without substituting defaults for missing evidence.</summary>
    private static string Required(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Catalog required metadata is missing.");
        }
        return value;
    }

    /// <summary>Rejects case-insensitive duplicate JSON property names recursively, including objects nested inside arrays.</summary>
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new ArgumentException("Duplicate catalog JSON property.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
    }
}

/// <summary>Versioned reference declaration; never a tenant file GUID or confirmed runtime Handler.</summary>
internal sealed record PlatformAspxCatalogEntry(string AspxKey, string Id, string DeploymentIdentity,
    string Culture, string Hive, string Path, IReadOnlyList<string> Paths, string DeclaredInherits, string HandlerEvidenceSource);
