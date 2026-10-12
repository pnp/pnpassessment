#nullable enable
using System.Text.Json;
using static PnP.Scanning.Core.Pipeline.Analysis.Audit.ClassicPageAuditAnalysis;

namespace PnP.Scanning.Core.Pipeline.Analysis.Audit.Legacy;

/// <summary>
/// Preserves event reduction and capped distinct-user counts for the mixed Classic scanner audit path.
/// <para>Evolved from <c>Scanners/Classic/Pages/AuditLogUsageAnalyzer.cs</c> to keep legacy audit reduction available after separating its Graph collection code.</para>
/// </summary>
internal static class ClassicPageLegacyAuditAnalysis
{
    /// <summary>
    /// Holds page event counts and the bounded user-hash set accumulated for one legacy query chunk.
    /// <para>Evolved from <c>Scanners/Classic/Pages/AuditLogUsageAnalyzer.cs</c> to keep legacy audit reduction available after separating its Graph collection code.</para>
    /// </summary>
    internal readonly record struct ChunkPageData(int ViewsCount, int CreatesCount, int EditsCount, HashSet<int> UserHashes);

    private const int MaxTrackedUsersPerPage = 10_000;

    /// <summary>
    /// Adds one Graph records page to the existing chunk statistics without replacing previously counted events.
    /// <para>Evolved from <c>Scanners/Classic/Pages/AuditLogUsageAnalyzer.cs</c> to keep legacy audit reduction available after separating its Graph collection code.</para>
    /// </summary>
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
    /// Combines chunk counts and bounded user sets while copying input sets to avoid mutating prior query results.
    /// <para>Evolved from <c>Scanners/Classic/Pages/AuditLogUsageAnalyzer.cs</c> to keep legacy audit reduction available after separating its Graph collection code.</para>
    /// </summary>
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
}
