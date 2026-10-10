#nullable enable
using System.Security.Cryptography;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts;

namespace PnP.Scanning.Core.Pipeline.Analysis;

internal sealed record ClassicAuditPageStats(string PageUrl, int Views, int Creates, int Edits, string[] UserHashes);

internal static class ClassicPageAuditAnalysis
{
    internal readonly record struct AuditPageStats(int ViewsCount, int CreatesCount, int EditsCount, int UniqueUsers);

    internal readonly record struct ChunkPageData(int ViewsCount, int CreatesCount, int EditsCount, HashSet<int> UserHashes);

    private const int MaxTrackedUsersPerPage = 10_000;

    internal static void AccumulateLegacyRecords(Dictionary<string, ChunkPageData> results, JsonElement records)
    {
        foreach (var record in records.EnumerateArray())
        {
            if (!record.TryGetProperty("operation", out var opProp)) continue;
            string operation = opProp.GetString() ?? string.Empty;
            if (!record.TryGetProperty("objectId", out var objProp)) continue;
            string? pageUrl = objProp.GetString();
            if (string.IsNullOrEmpty(pageUrl) || !pageUrl.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)) continue;
            string userId = record.TryGetProperty("userId", out var uidProp) ? uidProp.GetString() ?? string.Empty : string.Empty;
            if (!results.TryGetValue(pageUrl, out var existing)) existing = new(0, 0, 0, new HashSet<int>());
            results[pageUrl] = new(existing.ViewsCount + (operation.Equals("ClassicPageViewed", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                existing.CreatesCount + (operation.Equals("ClassicPageCreated", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                existing.EditsCount + (operation.Equals("ClassicPageEdited", StringComparison.OrdinalIgnoreCase) ? 1 : 0), existing.UserHashes);
            // Preserve the legacy count and memory cap. Snapshot analysis uses stable SHA-256 user hashes.
            if (!string.IsNullOrEmpty(userId) && existing.UserHashes.Count < MaxTrackedUsersPerPage)
                existing.UserHashes.Add(StringComparer.OrdinalIgnoreCase.GetHashCode(userId));
        }
    }

    /// <summary>
    /// Pure: applies audit stats to the record. If stats is null or the pageUrl key is not found,
    /// leaves counts at 0.
    /// </summary>
    internal static void ApplyAuditUsage(ClassicPageAuditUsageRow record, IReadOnlyDictionary<string, AuditPageStats>? stats, string? sourcePageUrl = null)
    {
        if (stats == null || !stats.TryGetValue(sourcePageUrl ?? record.PageUrl, out var pageStats))
            return;

        record.AuditViewsCount = pageStats.ViewsCount;
        record.AuditCreatesCount = pageStats.CreatesCount;
        record.AuditEditsCount = pageStats.EditsCount;
        record.AuditUniqueUsers = pageStats.UniqueUsers;
    }

    internal static IReadOnlyDictionary<string, AuditPageStats> MergeChunks(
        IEnumerable<IReadOnlyDictionary<string, ChunkPageData>> chunks)
    {
        var merged = new Dictionary<string, (int Views, int Creates, int Edits, HashSet<int> Users)>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunks)
        {
            foreach (var kvp in chunk)
            {
                if (!merged.TryGetValue(kvp.Key, out var existing))
                {
                    // Copy so we never alias (or later mutate) the input chunk's own set.
                    // The chunk set is already ≤ MaxTrackedUsersPerPage (capped at fetch time).
                    merged[kvp.Key] = (kvp.Value.ViewsCount, kvp.Value.CreatesCount, kvp.Value.EditsCount, new HashSet<int>(kvp.Value.UserHashes));
                }
                else
                {
                    // Re-apply the per-page cap on the merged (cross-chunk) set: a hot page appearing
                    // in every chunk could otherwise accumulate up to chunks × MaxTrackedUsersPerPage
                    // hashes. Stop unioning once the merged set reaches the cap — beyond that the page
                    // is clearly "heavily used" and the exact distinct count matters less, while memory
                    // stays bounded to MaxTrackedUsersPerPage × 4 bytes per page.
                    foreach (var userHash in kvp.Value.UserHashes)
                    {
                        if (existing.Users.Count >= MaxTrackedUsersPerPage) break;
                        existing.Users.Add(userHash);
                    }
                    merged[kvp.Key] = (
                        existing.Views   + kvp.Value.ViewsCount,
                        existing.Creates + kvp.Value.CreatesCount,
                        existing.Edits   + kvp.Value.EditsCount,
                        existing.Users);
                }
            }
        }
        return merged.ToDictionary(
            kvp => kvp.Key,
            kvp => new AuditPageStats(kvp.Value.Views, kvp.Value.Creates, kvp.Value.Edits, kvp.Value.Users.Count),
            StringComparer.OrdinalIgnoreCase);
    }

    internal static ClassicAuditPageStats[] Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new SnapshotIntegrityException("Succeeded audit query has no records array.");
        var results = new Dictionary<string, (int Views, int Creates, int Edits, HashSet<string> Users)>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in values.EnumerateArray())
        {
            if (!record.TryGetProperty("operation", out var operation) || !record.TryGetProperty("objectId", out var objectId)) continue;
            var url = objectId.GetString(); var op = operation.GetString();
            if (string.IsNullOrEmpty(url) || !url.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)) continue;
            if (!results.TryGetValue(url, out var value)) value = (0, 0, 0, new(StringComparer.Ordinal));
            if (record.TryGetProperty("userId", out var user) && !string.IsNullOrEmpty(user.GetString()) && value.Users.Count < 10_000)
                value.Users.Add(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(user.GetString()!.ToUpperInvariant()))));
            results[url] = (value.Views + (string.Equals(op, "ClassicPageViewed", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                value.Creates + (string.Equals(op, "ClassicPageCreated", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                value.Edits + (string.Equals(op, "ClassicPageEdited", StringComparison.OrdinalIgnoreCase) ? 1 : 0), value.Users);
        }
        return results.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ClassicAuditPageStats(x.Key, x.Value.Views, x.Value.Creates, x.Value.Edits, x.Value.Users.OrderBy(y => y, StringComparer.Ordinal).ToArray())).ToArray();
    }
}
