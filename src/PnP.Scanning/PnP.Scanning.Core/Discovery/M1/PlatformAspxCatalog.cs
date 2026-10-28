using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PnP.Scanning.Core.Discovery.M1;

/// <summary>Explicit versioned catalog evidence, frozen as exact input bytes with the Scan properties.</summary>
internal sealed class PlatformAspxCatalog
{
    internal const string Schema = "platform-aspx-catalog/v1";
    internal const int MaximumBytes = 16 * 1024 * 1024;
    internal const int MaximumTransportMessageBytes = 32 * 1024 * 1024;
    internal string Version { get; }
    internal string Source { get; }
    internal string Digest { get; }
    internal string FrozenPayload { get; }
    internal IReadOnlyList<PlatformAspxCatalogEntry> Entries { get; }

    private PlatformAspxCatalog(string version, string source, byte[] bytes, List<PlatformAspxCatalogEntry> entries)
    {
        Version = version;
        Source = source;
        Digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        FrozenPayload = Convert.ToBase64String(bytes);
        Entries = entries.AsReadOnly();
    }

    internal static PlatformAspxCatalog Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumBytes) throw new ArgumentException("Catalog is missing or exceeds the supported size.", nameof(path));
        return Parse(File.ReadAllBytes(path));
    }

    internal static PlatformAspxCatalog FromFrozenPayload(string payload) => Parse(Convert.FromBase64String(payload));

    internal static PlatformAspxCatalog Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new ArgumentException("Invalid catalog size.", nameof(bytes));
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        using var document = JsonDocument.Parse(text);
        RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        if (Required(root, "schemaVersion") != Schema) throw new ArgumentException("Unsupported platform ASPX catalog schema.");
        var version = Required(root, "catalogVersion");
        var source = Required(root, "source");
        var rows = root.GetProperty("entries");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 65536) throw new ArgumentException("Invalid catalog entries.");
        var entries = new List<PlatformAspxCatalogEntry>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            var id = Required(row, "id");
            var deployment = Required(row, "deploymentIdentity");
            var culture = Required(row, "culture");
            var path = NormalizePath(Required(row, "path"));
            var hive = Hive(path);
            var aliases = new List<string>();
            if (row.TryGetProperty("aliases", out var aliasRows))
            {
                if (aliasRows.ValueKind != JsonValueKind.Array) throw new ArgumentException("Catalog aliases must be an array.");
                foreach (var alias in aliasRows.EnumerateArray())
                {
                    var normalized = NormalizePath(alias.GetString());
                    if (Hive(normalized) != hive) throw new ArgumentException("Catalog aliases cannot collapse separate layout hives.");
                    aliases.Add(normalized);
                }
            }
            var keyMaterial = JsonSerializer.Serialize(new[] { version, deployment, culture, hive, id });
            var key = "platform:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial))).ToLowerInvariant();
            if (!identities.Add(key)) throw new ArgumentException("Duplicate platform catalog identity.");
            var inherits = row.TryGetProperty("declaredInherits", out var declared) ? declared.GetString() : null;
            var evidence = row.TryGetProperty("handlerEvidenceSource", out var evidenceValue) ? evidenceValue.GetString() : null;
            if ((inherits != null) != !string.IsNullOrWhiteSpace(evidence) || inherits != null && string.IsNullOrWhiteSpace(inherits))
                throw new ArgumentException("Catalog handler declarations require an explicit versioned evidence source.");
            entries.Add(new PlatformAspxCatalogEntry(key, id, deployment, culture, hive, path,
                aliases.Append(path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToArray(), inherits, evidence));
        }
        return new PlatformAspxCatalog(version, source, bytes, entries);
    }

    internal IReadOnlyList<PlatformAspxCatalogEntry> ResolvePath(string path)
    {
        var normalized = NormalizePath(path);
        return Entries.Where(entry => entry.Paths.Contains(normalized, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/_layouts/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains('\\') || path.Contains('?') || path.Contains('#') || !path.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(path, "%2f|%5c", RegexOptions.IgnoreCase)) throw new ArgumentException("Catalog member must be a query-independent layout ASPX path.");
        var decoded = Uri.UnescapeDataString(path);
        if (decoded.Contains('?') || decoded.Contains('#') || decoded.Contains('\\') ||
            decoded.Split('/').Any(part => part is "." or "..") || Regex.IsMatch(decoded, "%2f|%5c", RegexOptions.IgnoreCase) || decoded.Any(char.IsControl))
            throw new ArgumentException("Invalid catalog path segments.");
        var normalized = decoded.ToLowerInvariant();
        _ = Hive(normalized);
        return normalized;
    }

    private static string Hive(string path)
    {
        var first = path["/_layouts/".Length..].Split('/')[0];
        if (first == "15") return "hive16";
        if (first.All(char.IsDigit)) throw new ArgumentException("Only /_layouts/ and /_layouts/15/ namespaces are supported; do not invent a /16/ alias.");
        return "hive14";
    }

    private static string Required(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Catalog required metadata is missing.");
        return value;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate catalog JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}

/// <summary>Versioned reference declaration; never a tenant file GUID or confirmed runtime Handler.</summary>
internal sealed record PlatformAspxCatalogEntry(string AspxKey, string Id, string DeploymentIdentity,
    string Culture, string Hive, string Path, IReadOnlyList<string> Paths, string DeclaredInherits, string HandlerEvidenceSource);
