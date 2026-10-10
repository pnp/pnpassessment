#nullable enable
using System.Security.Cryptography;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Audit;
using PnP.Scanning.Core.Pipeline.Contracts.Module;

namespace PnP.Scanning.Core.Pipeline.Analysis.Audit;

internal sealed record ClassicAuditPageStats(string PageUrl, int Views, int Creates, int Edits, string[] UserHashes);

internal static class ClassicPageAuditAnalysis
{
    internal readonly record struct AuditPageStats(int ViewsCount, int CreatesCount, int EditsCount, int UniqueUsers);

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
