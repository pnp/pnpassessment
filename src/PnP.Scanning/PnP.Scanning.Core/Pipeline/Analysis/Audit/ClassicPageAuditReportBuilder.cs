using ClassicPageAuditUsage = PnP.Scanning.Core.Pipeline.Contracts.Audit.ClassicPageAuditUsageRow;
using PnP.Scanning.Core.Pipeline.Contracts.Audit;
using PnP.Scanning.Core.Pipeline.Contracts.Module;

namespace PnP.Scanning.Core.Pipeline.Analysis.Audit;

/// <summary>
/// Builds page audit usage and coverage rows from saved statistics and the fixed collection audit window.
/// <para>Evolved from <c>Pipeline/Analysis/Module/ClassicPageReportBuilder.cs</c> to keep audit result aggregation in the Audit subject.</para>
/// </summary>
internal static class ClassicPageAuditReportBuilder
{
    /// <summary>
    /// Combines settled audit chunks and page statistics into per-site usage rows with explicit partial or empty coverage outcomes.
    /// <para>Evolved from <c>Pipeline/Analysis/Module/ClassicPageReportBuilder.cs</c> to separate audit calculations from report publication.</para>
    /// </summary>
    internal static List<ClassicPageAuditUsage> Build(Guid assessmentId, ClassicPageScopeSource scope,
        ClassicPageAuditChunkSource[] chunks, List<ClassicAuditPageStats> inputs)
    {
        var succeeded = chunks.Count(x => x.Status == "succeeded");
        var status = succeeded == chunks.Length && chunks.Length > 0 ? "succeeded" : succeeded > 0 ? "partial"
            : chunks.All(x => x.Status == "skipped") ? "skipped" : "failed";
        var reason = string.Join("; ", chunks.Where(x => x.Error != null).OrderBy(x => x.Chunk).Select(x => x.Error));
        var merged = inputs.GroupBy(x => x.PageUrl, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
            group => new ClassicPageAuditAnalysis.AuditPageStats(group.Sum(x => x.Views), group.Sum(x => x.Creates),
                group.Sum(x => x.Edits), group.SelectMany(x => x.UserHashes).Distinct().Take(10_000).Count()), StringComparer.OrdinalIgnoreCase);
        var sites = scope.Sites.OrderByDescending(x => x.Length).ToArray();
        var result = new List<ClassicPageAuditUsage>();
        foreach (var site in scope.Sites)
        {
            var stats = merged.Where(x => sites.FirstOrDefault(s => x.Key.StartsWith(s.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
                || x.Key.Equals(s, StringComparison.OrdinalIgnoreCase)) == site).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            // Preserve server-relative page URLs in the existing audit CSV shape.
            string Relative(string absolute) { var origin = new Uri(site).GetLeftPart(UriPartial.Authority); var path = absolute.StartsWith(origin, StringComparison.OrdinalIgnoreCase) ? absolute[origin.Length..] : absolute; return path.Length == 0 ? "/" : path; }
            // Initialize page or coverage rows with the same pinned audit window and settled query outcome.
            ClassicPageAuditUsage Row(string url) => new() { ScanId = assessmentId, SiteUrl = site, WebUrl = "/", PageUrl = Relative(url),
                AuditWindowStart = scope.AuditWindowStart, AuditWindowEnd = scope.AuditWindowEnd, QueryStatus = status, SkipReason = string.IsNullOrEmpty(reason) ? null : reason };
            foreach (var value in stats)
            {
                var row = Row(value.Key);
                ClassicPageAuditAnalysis.ApplyAuditUsage(row, stats, value.Key);
                if (status != "partial") row.SkipReason = null;
                result.Add(row);
            }
            if (stats.Count == 0 || status == "partial") result.Add(Row(site));
        }
        return result;
    }
}
