using ClassicPage = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageRow;
using ClassicWebSummary = PnP.Scanning.Core.Pipeline.Contracts.Web.ClassicWebSummaryRow;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Analysis.Site;
using PnP.Scanning.Core.Pipeline.Contracts.Web;

namespace PnP.Scanning.Core.Pipeline.Analysis.Web;

internal static class ClassicWebSummaryBuilder
{
    internal static ClassicWebSummary Build(Guid assessmentId, ClassicPageWebSource source, IReadOnlyList<ClassicPage> pages, int modernPages)
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
}
