#nullable enable
using ClassicPage = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageRow;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

/// <summary>
/// Projects captured blog list items into page report rows using fixed selection and user-information options.
/// <para>Evolved from <c>Pipeline/Analysis/Module/ClassicPageAnalysisModule.cs</c> to keep blog page rules in the Page subject.</para>
/// </summary>
internal static class ClassicBlogPageProjection
{
    /// <summary>
    /// Builds one blog page row from saved item fields or excludes it when the fixed home-page selection does not match.
    /// <para>Evolved from <c>Pipeline/Analysis/Module/ClassicPageAnalysisModule.cs</c> to separate item projection from module dispatch.</para>
    /// </summary>
    internal static ClassicPage? Analyze(Guid assessmentId, ClassicPageBlogBatchSource input,
        Dictionary<string, SourceField> fields, ClassicPageWebSource web, ClassicPageSourceOptions options)
    {
        var url = fields.GetValueOrDefault("FileRef")?.Text ?? fields.GetValueOrDefault("ID")?.Text ?? "";
        var home = web.WelcomePageState.Succeeded ? HomePageDetector.IsHomePage(url, web.WelcomePage) : (bool?)null;
        if (options.HomePageOnly && home != true) return null;
        return new ClassicPage
        {
            ScanId = assessmentId, SiteUrl = input.SiteUrl, WebUrl = input.WebUrl, PageUrl = url, PageName = fields.GetValueOrDefault("Title")?.Text ?? "",
            ListId = input.ListId, ListTitle = input.ListTitle, ListUrl = input.ListUrl,
            ModifiedAt = fields.TryGetValue("Modified", out var modified) && modified.ToValue() is DateTime date ? date : default,
            ModifiedBy = options.SkipUserInformation ? null : ClassicPageProjection.ModifiedBy(fields),
            PageType = ClassicPageRules.BlogPage, HomePage = home, RemediationCode = "CP4",
        };
    }
}
