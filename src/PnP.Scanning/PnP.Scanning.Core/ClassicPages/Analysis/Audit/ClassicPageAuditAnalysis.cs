#nullable enable
using System.Security.Cryptography;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;

namespace PnP.Scanning.Core.ClassicPages.Analysis.Audit;

/// <summary>
/// Retains audit event counts and stable user hashes reduced from one saved response.
/// </summary>
internal sealed record ClassicAuditPageStats(string PageUrl, int Views, int Creates, int Edits, string[] UserHashes);

/// <summary>
/// Reduces saved audit responses into page statistics without online access.
/// </summary>
internal static class ClassicPageAuditAnalysis
{
    internal static IEqualityComparer<string> LocatorComparer { get; } = new FullLocatorComparer();

    /// <summary>Preserves the original path-case policy while comparing query/fragment suffixes exactly, without parameter reordering.</summary>
    internal static bool LocatorEquals(string? left, string? right) => LocatorComparer.Equals(left, right);

    /// <summary>Builds a comparison-only key with the historical path-case policy and an opaque unchanged query/fragment suffix.</summary>
    internal static string LocatorKey(string value)
    {
        int delimiter = value.IndexOfAny(['?', '#']);
        return delimiter < 0 ? value.ToUpperInvariant() : value[..delimiter].ToUpperInvariant() + value[delimiter..];
    }

    /// <summary>
    /// Reduces one saved audit records response into per-page event counts and user hashes.
    /// </summary>
    internal static ClassicAuditPageStats[] Parse(string body)
    {
        using JsonDocument? doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("value", out JsonElement values) || values.ValueKind != JsonValueKind.Array)
            throw new SnapshotIntegrityException("Succeeded audit query has no records array.");
        Dictionary<string, (int Views, int Creates, int Edits, HashSet<string> Users)>? results =
            new Dictionary<string, (int Views, int Creates, int Edits, HashSet<string> Users)>(LocatorComparer);
        foreach (JsonElement record in values.EnumerateArray())
        {
            if (!record.TryGetProperty("operation", out JsonElement operation) ||
                !record.TryGetProperty("objectId", out JsonElement objectId))
                continue;
            string? url = objectId.GetString();
            string? op = operation.GetString();
            if (string.IsNullOrEmpty(url) || !IsAspxLocator(url)) continue;
            if (!results.TryGetValue(url, out (int Views, int Creates, int Edits, HashSet<string> Users) value))
                value = (0, 0, 0, new(StringComparer.Ordinal));
            if (record.TryGetProperty("userId", out JsonElement user) && !string.IsNullOrEmpty(user.GetString()) &&
                value.Users.Count < 10_000)
                value.Users.Add(
                    Convert.ToHexString(
                        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(user.GetString()!.ToUpperInvariant()))));
            results[url] = (
                value.Views + (string.Equals(op, "ClassicPageViewed", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                value.Creates + (string.Equals(op, "ClassicPageCreated", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                value.Edits + (string.Equals(op, "ClassicPageEdited", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                value.Users);
        }

        return results.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new ClassicAuditPageStats(
                x.Key,
                x.Value.Views,
                x.Value.Creates,
                x.Value.Edits,
                x.Value.Users.OrderBy(y => y, StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Checks the URI path only while retaining the original complete event locator as the aggregation key.
    /// Query values cannot turn a non-ASPX path into a page or collapse independent Posts into their renderer.
    /// </summary>
    private static bool IsAspxLocator(string locator)
    {
        Uri? uri;
        if (locator.StartsWith('/') && !locator.StartsWith("//", StringComparison.Ordinal))
        {
            if (locator.Contains('\\') || !Uri.TryCreate(new Uri("https://audit.invalid"), locator, out uri) ||
                uri.Host != "audit.invalid")
                return false;
        }
        else if (!Uri.TryCreate(locator, UriKind.Absolute, out uri) || uri.Scheme is not ("https" or "http"))
            return false;

        return ClassicPageRules.IsAspxFile(null, uri.AbsolutePath);
    }

    /// <summary>Retains the full item-address suffix as opaque observed evidence while keeping historical case-insensitive path comparison.</summary>
    private sealed class FullLocatorComparer : IEqualityComparer<string>
    {
        /// <summary>Compares path/authority without case and the complete unmodified query/fragment suffix with ordinal semantics.</summary>
        public bool Equals(string? left, string? right)
        {
            if (left == null || right == null) return left == right;
            return string.Equals(LocatorKey(left), LocatorKey(right), StringComparison.Ordinal);
        }

        /// <summary>Hashes the same path and opaque-suffix components used by equality.</summary>
        public int GetHashCode(string value)
        {
            return StringComparer.Ordinal.GetHashCode(LocatorKey(value));
        }
    }
}