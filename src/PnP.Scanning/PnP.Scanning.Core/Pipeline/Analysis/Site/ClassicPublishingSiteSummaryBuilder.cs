using ClassicPage = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageRow;
using ClassicPublishingSiteSummary = PnP.Scanning.Core.Pipeline.Contracts.Site.ClassicPublishingSiteSummaryRow;
using ClassicWebSummary = PnP.Scanning.Core.Pipeline.Contracts.Web.ClassicWebSummaryRow;
using PnP.Scanning.Core.Pipeline.Analysis.Page;

namespace PnP.Scanning.Core.Pipeline.Analysis.Site;

/// <summary>
/// Builds publishing-site page counts, used layouts and latest update dates from detached web and page projections.
/// <para>Evolved from <c>Pipeline/Analysis/Module/ClassicPageReportBuilder.cs</c> to keep publishing-site aggregation in the Site subject.</para>
/// </summary>
internal static class ClassicPublishingSiteSummaryBuilder
{
    /// <summary>
    /// Groups publishing webs by site and produces the existing publishing summary report rows from their analyzed pages.
    /// <para>Evolved from <c>Pipeline/Analysis/Module/ClassicPageReportBuilder.cs</c> to separate site calculations from report publication.</para>
    /// </summary>
    internal static List<ClassicPublishingSiteSummary> Build(Guid assessmentId,
        IEnumerable<ClassicWebSummary> summaries, IReadOnlyCollection<ClassicPage> pages)
    {
        var publishing = new List<ClassicPublishingSiteSummary>();
        foreach (var group in summaries.Where(x => x.IsClassicPublishingSite || x.ClassicPublishingPages > 0).GroupBy(x => x.SiteUrl))
        {
            var relevant = pages.Where(x => x.SiteUrl == group.Key && x.PageType == ClassicPageRules.PublishingPage).ToArray();
            var dates = relevant.Where(x => x.ModifiedAt != DateTime.MinValue && x.ModifiedAt != DateTime.MaxValue).Select(x => x.ModifiedAt).ToArray();
            var layouts = relevant.Where(x => !string.IsNullOrEmpty(x.Layout)).Select(x => x.Layout).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            publishing.Add(new() { ScanId = assessmentId, SiteUrl = group.Key, NumberOfWebs = group.Count(),
                NumberOfPages = group.Sum(x => x.ClassicPublishingPages), UsedPageLayouts = layouts.Length > 0 ? string.Join(',', layouts) : null,
                LastPageUpdateDate = dates.Length > 0 ? dates.Max() : null });
        }
        return publishing;
    }
}
