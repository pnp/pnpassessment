using ClassicPage = PnP.Scanning.Core.Pipeline.Contracts.ClassicPageRow;
using ClassicWebSummary = PnP.Scanning.Core.Pipeline.Contracts.ClassicWebSummaryRow;
using ClassicSiteSummary = PnP.Scanning.Core.Pipeline.Contracts.ClassicSiteSummaryRow;
using Web = PnP.Scanning.Core.Pipeline.Contracts.WebRow;
using PnP.Scanning.Core.Pipeline.Contracts;


namespace PnP.Scanning.Core.Pipeline.Analysis;

internal static class ClassicPageSummaryBuilder
{
    internal static ClassicWebSummary Web(Guid assessmentId, ClassicPageWebSource source, IReadOnlyList<ClassicPage> pages, int modernPages)
    {
        var withParts = pages.Where(x => x.WebPartCount > 0).ToArray();
        var siteType = ClassicSiteRules.GetSiteType(source.Template);
        var codes = pages.Select(x => x.RemediationCode).Where(x => !string.IsNullOrEmpty(x)).ToHashSet();
        var remediation = siteType == SiteType.Publishing ? "CS2" : siteType == SiteType.Blog ? "CS1" : null;
        if (remediation != null) codes.Add(remediation);
        return new ClassicWebSummary
        {
            ScanId = assessmentId, SiteUrl = source.SiteUrl, WebUrl = source.WebUrl, Template = source.Template,
            LastItemUserModifiedDate = source.LastItemUserModifiedDate, ModernPages = modernPages,
            ClassicASPXPages = pages.Count(x => x.PageType == ClassicPageRules.ASPXPage),
            ClassicWikiPages = pages.Count(x => x.PageType == ClassicPageRules.WikiPage),
            ClassicBlogPages = pages.Count(x => x.PageType == ClassicPageRules.BlogPage),
            ClassicWebPartPages = pages.Count(x => x.PageType == ClassicPageRules.WebPartPage),
            ClassicPublishingPages = pages.Count(x => x.PageType == ClassicPageRules.PublishingPage),
            ClassicPages = pages.Count(x => x.PageType is ClassicPageRules.ASPXPage or ClassicPageRules.WikiPage or ClassicPageRules.BlogPage or ClassicPageRules.WebPartPage or ClassicPageRules.PublishingPage),
            PagesWithWebParts = withParts.Length, MappableWebPartPages = withParts.Count(x => x.MappingPercentage >= 100),
            UnmappedWebPartPages = withParts.Count(x => x.MappingPercentage < 100),
            AvgMappingPercentage = withParts.Length == 0 ? 0 : withParts.Average(x => x.MappingPercentage),
            UncustomizedHomePages = pages.Count(x => x.UncustomizedHomePage),
            IsModernSite = siteType == SiteType.Modern, IsClassicPublishingSite = siteType == SiteType.Publishing,
            IsModernCommunicationSite = siteType == SiteType.Communication, RemediationCode = remediation,
            AggregatedRemediationCodes = string.Join(',', codes.OrderBy(x => x, StringComparer.Ordinal)),
        };
    }
    internal static List<ClassicSiteSummary> Sites(Guid assessmentId, IEnumerable<ClassicWebSummary> webs)
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
