using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using ClassicPage = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageRow;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using System.Globalization;

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

internal static class ClassicBlogPageProjection
{
    internal static ClassicPage? Analyze(Guid assessmentId, ClassicPageBlogBatchSource input,
        Dictionary<string, SourceField> fields, ClassicPageWebSource web, ClassicPageSourceOptions options)
    {
        if (options.HomePageOnly) return null;
        if (!int.TryParse(fields.GetValueOrDefault("ID")?.Text, NumberStyles.None,
            CultureInfo.InvariantCulture, out var itemId) || itemId <= 0 ||
            input.ListId == Guid.Empty || web.SiteId == Guid.Empty || web.WebId == Guid.Empty)
            throw new InvalidDataException("A Blog/Delve content row requires its observed site, Web, list and item identity.");
        if (input.PageType is not (ClassicPageRules.BlogPage or ClassicPageRules.DelveBlogPage or ClassicPageRules.ASPXPage))
            throw new InvalidDataException("The selected content batch has an unsupported Blog type.");
        var delve = input.PageType == ClassicPageRules.DelveBlogPage;
        string? url;
        if (delve)
        {
            if (!string.Equals(fields.GetValueOrDefault(ClassicPageRules.FileTypeField)?.Text,
                ClassicPageRules.DelveBlogFileType, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A Delve Blog batch returned a non-pointpub item.");
            url = fields.GetValueOrDefault("FileRef")?.Text;
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal) ||
                !PnP.Scanning.Core.Discovery.SharePointAspxUrlPolicy.IsPathWithinRoot(url, input.ListUrl))
                throw new InvalidDataException("A Delve Blog item requires its returned server-relative FileRef.");
        }
        else
        {
            var contentType = fields.GetValueOrDefault("ContentTypeId")?.Text ?? "";
            var form = input.ContentTypeForms?.OrderByDescending(type => type.ContentTypeId.Length)
                .FirstOrDefault(type => contentType.StartsWith(type.ContentTypeId, StringComparison.OrdinalIgnoreCase))?.FormUrl
                ?? input.DisplayFormUrl;
            if (input.PageType == ClassicPageRules.ASPXPage && !LegacyDisplayFormAssessment.IsContentPageForm(form))
                return null;
            if (form != null && (!form.StartsWith('/') || form.StartsWith("//", StringComparison.Ordinal)))
                throw new InvalidDataException("A captured Blog form declaration must be normalized before offline analysis.");
            url = form == null ? null : form + (form.Contains('?') ? "&" : "?") + "ID=" + itemId.ToString(CultureInfo.InvariantCulture);
        }
        return new ClassicPage
        {
            ScanId = assessmentId, SiteUrl = input.SiteUrl, WebUrl = input.WebUrl, PageUrl = url!, PageName = fields.GetValueOrDefault("Title")?.Text ?? "",
            ListId = input.ListId, ListTitle = input.ListTitle, ListUrl = input.ListUrl,
            SiteCollectionId = web.SiteId, WebId = web.WebId, ListItemId = itemId,
            DiscoveryStatus = "Discovered", AssessmentStatus = url == null ? "Unknown" : "Complete",
            ModifiedAt = fields.TryGetValue("Modified", out var modified) && modified.ToValue() is DateTime date ? date : default,
            ModifiedBy = options.SkipUserInformation ? null : ClassicPageProjection.ModifiedBy(fields),
            PageType = input.PageType, HomePage = false, RemediationCode = delve ? null :
                input.PageType == ClassicPageRules.BlogPage ? "CP4" : "CP5",
        };
    }
}
