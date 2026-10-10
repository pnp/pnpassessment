using ClassicWebSummary = PnP.Scanning.Core.Pipeline.Contracts.Web.ClassicWebSummaryRow;
using ClassicSiteSummary = PnP.Scanning.Core.Pipeline.Contracts.Site.ClassicSiteSummaryRow;

namespace PnP.Scanning.Core.Pipeline.Analysis.Site;

internal static class ClassicSiteSummaryBuilder
{
    internal static List<ClassicSiteSummary> Build(Guid assessmentId, IEnumerable<ClassicWebSummary> webs)
    {
        var result = new List<ClassicSiteSummary>();
        foreach (var group in webs.OrderBy(x => x.SiteUrl).ThenBy(x => x.WebUrl).GroupBy(x => x.SiteUrl))
        {
            var root = group.FirstOrDefault(x => x.WebUrl == "/");
            var sub = group.Where(x => x.WebUrl != "/").ToArray();
            var count = group.Sum(x => x.PagesWithWebParts);
            result.Add(new ClassicSiteSummary
            {
                ScanId = assessmentId, SiteUrl = group.Key, RootWebTemplate = root?.Template,
                SubWebTemplates = sub.Length == 0 ? null : string.Join(',', sub.Select(x => x.Template).Distinct()),
                SubWebCount = sub.Length, SubWebDepth = sub.Length == 0 ? 0 : sub.Max(x => x.WebUrl.Count(c => c == '/')),
                LastItemUserModifiedDate = group.Max(x => x.LastItemUserModifiedDate),
                ClassicLists = group.Sum(x => x.ClassicLists), ModernLists = group.Sum(x => x.ModernLists),
                ClassicPages = group.Sum(x => x.ClassicPages), ModernPages = group.Sum(x => x.ModernPages),
                ClassicASPXPages = group.Sum(x => x.ClassicASPXPages), ClassicBlogPages = group.Sum(x => x.ClassicBlogPages),
                ClassicWikiPages = group.Sum(x => x.ClassicWikiPages), ClassicWebPartPages = group.Sum(x => x.ClassicWebPartPages),
                ClassicPublishingPages = group.Sum(x => x.ClassicPublishingPages),
                PagesWithWebParts = count, MappableWebPartPages = group.Sum(x => x.MappableWebPartPages),
                UnmappedWebPartPages = group.Sum(x => x.UnmappedWebPartPages), UncustomizedHomePages = group.Sum(x => x.UncustomizedHomePages),
                AvgMappingPercentage = count == 0 ? 0 : group.Sum(x => x.AvgMappingPercentage * x.PagesWithWebParts) / count,
                ClassicWorkflows = group.Sum(x => x.ClassicWorkflows), ClassicInfoPathForms = group.Sum(x => x.ClassicInfoPathForms),
                ClassicExtensibilities = group.Sum(x => x.ClassicExtensibilities), SharePointAddIns = group.Sum(x => x.SharePointAddIns),
                AzureACSPrincipals = group.Sum(x => x.AzureACSPrincipals),
                AggregatedRemediationCodes = string.Join(',', group.SelectMany(x => (x.AggregatedRemediationCodes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)).Distinct()),
            });
        }
        return result;
    }
}
