#nullable enable
using System.Security.Cryptography;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Module;

namespace PnP.Scanning.Core.Pipeline.Analysis.Audit;

/// <summary>
/// Retains audit event counts and stable user hashes reduced from one saved response.
/// </summary>
internal sealed record ClassicAuditPageStats(string PageUrl, int Views, int Creates, int Edits, string[] UserHashes);

/// <summary>
/// Reduces saved audit responses into page statistics without online access.
/// </summary>
internal static class ClassicPageAuditAnalysis
{
    /// <summary>
    /// Reduces one saved audit records response into per-page event counts and user hashes.
    /// </summary>
    internal static ClassicAuditPageStats[] Parse(string body)
    {
        using JsonDocument? doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("value", out JsonElement values) || values.ValueKind != JsonValueKind.Array)
            throw new SnapshotIntegrityException("Succeeded audit query has no records array.");
        Dictionary<string, (int Views, int Creates, int Edits, HashSet<string> Users)>? results =
            new Dictionary<string, (int Views, int Creates, int Edits, HashSet<string> Users)>(
                StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement record in values.EnumerateArray())
        {
            if (!record.TryGetProperty("operation", out JsonElement operation) ||
                !record.TryGetProperty("objectId", out JsonElement objectId))
                continue;
            string? url = objectId.GetString();
            string? op = operation.GetString();
            if (string.IsNullOrEmpty(url) || !url.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)) continue;
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
            .Select(x => new ClassicAuditPageStats(
                x.Key,
                x.Value.Views,
                x.Value.Creates,
                x.Value.Edits,
                x.Value.Users.OrderBy(y => y, StringComparer.Ordinal).ToArray()))
            .ToArray();
    }
}