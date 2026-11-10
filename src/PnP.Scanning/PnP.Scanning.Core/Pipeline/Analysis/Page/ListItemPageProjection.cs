#nullable enable
using System.Globalization;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Pipeline.Analysis.Page;

/// <summary>
/// Converts returned objects into the same Page contract, distinguishing independent items from verified file-backed content.
/// </summary>
internal static class ListItemPageProjection
{
    /// <summary>
    /// Projects content only from observed item and native File evidence; unresolved file links remain evidence, never item Pages.
    /// </summary>
    internal static ClassicPageRow? Analyze(
        Guid assessmentId,
        ListItemBatch batch,
        ListItemFacts item,
        WebFacts web,
        ClassicCollectionOptions options)
    {
        if (options.HomePageOnly) return null;
        if (batch.ListId == Guid.Empty || item.ListItemId <= 0 || item.Fields.GetValueOrDefault("ID")?.Text !=
            item.ListItemId.ToString(CultureInfo.InvariantCulture))
            throw new SnapshotIntegrityException("Content projection requires the returned native item identity.");
        Dictionary<string, SourceField> fields = item.Fields;
        bool fileBacked = batch.QueryKind == ListItemQueryKind.PointPublishingFiles;
        PhysicalFileFacts? file = null;
        string? locator;
        string pageType;
        PageIdentity identity;
        if (fileBacked)
        {
            if (!string.Equals(
                    fields.GetValueOrDefault(ClassicPageRules.FileTypeField)?.Text,
                    "pointpub",
                    StringComparison.OrdinalIgnoreCase))
                throw new SnapshotIntegrityException(
                    "The point-publishing query returned an item without pointpub evidence.");
            if (item.FileAssociation is not { State.Succeeded: true, FileFacts: PhysicalFileFacts linked }) return null;
            if (linked.SiteCollectionId != web.SiteCollectionId || linked.WebId != web.WebId ||
                linked.ListId != batch.ListId || linked.ListItemId != item.ListItemId)
                throw new SnapshotIntegrityException(
                    "Point-publishing content has a foreign native File relationship.");
            file = linked;
            identity = PageIdentity.File(linked.SiteCollectionId, linked.FileUniqueId);
            locator = linked.ServerRelativeUrl;
            pageType = ClassicPageRules.DelveBlogPage;
        }
        else
        {
            if (batch.QueryKind == ListItemQueryKind.Posts && batch.ListTemplate != 301)
                throw new SnapshotIntegrityException("Blog item projection requires an observed Posts list template.");
            if (batch.QueryKind is not (ListItemQueryKind.Posts or ListItemQueryKind.CustomDisplayForms))
                throw new SnapshotIntegrityException("Unsupported independent-item query kind.");
            identity = PageIdentity.Item(web.SiteCollectionId, batch.ListId, item.ListItemId);
            string contentType = fields.GetValueOrDefault("ContentTypeId")?.Text ?? "";
            string? renderer = batch.ContentTypeForms?.OrderByDescending(form => form.ContentTypeId.Length)
                .FirstOrDefault(form => contentType.StartsWith(form.ContentTypeId, StringComparison.OrdinalIgnoreCase))
                ?.FormUrl ?? batch.DisplayFormUrl;
            if (batch.QueryKind == ListItemQueryKind.CustomDisplayForms &&
                !LegacyDisplayFormAssessment.IsContentPageForm(renderer))
                return null;
            if (renderer != null && (!renderer.StartsWith('/') || renderer.StartsWith("//", StringComparison.Ordinal)))
                throw new SnapshotIntegrityException("The captured renderer is not a normalized declaration.");
            locator = renderer == null
                ? null
                : renderer + (renderer.Contains('?') ? "&" : "?") + "ID=" +
                  item.ListItemId.ToString(CultureInfo.InvariantCulture);
            pageType = batch.ListTemplate == 301 ? ClassicPageRules.BlogPage : ClassicPageRules.ASPXPage;
        }

        return new ClassicPageRow
        {
            Identity = identity,
            ScanId = assessmentId,
            SiteCollectionId = web.SiteCollectionId,
            WebId = web.WebId,
            SiteUrl = batch.SiteUrl,
            WebUrl = batch.WebUrl,
            FileUniqueId = file?.FileUniqueId,
            ListId = batch.ListId,
            ListItemId = item.ListItemId,
            ListTitle = batch.ListTitle,
            ListUrl = batch.ListUrl,
            ListTemplate = batch.ListTemplate,
            PageUrl = locator!,
            PageName = fields.GetValueOrDefault("Title")?.Text ?? "",
            PageType = pageType,
            AssessmentPageType = pageType,
            SourceKind = fileBacked ? "File" : "ListItem",
            ContentReadState = fileBacked ? item.FileAssociation!.ContentReadState : "NotApplicable",
            ClassificationReason =
                fileBacked ? "ObservedPointPublishingItemAndNativeFile" : "ObservedListItemAndRendererDeclaration",
            DiscoveryStatus = "Discovered",
            AssessmentStatus = locator == null ? "Unknown" : "Complete",
            HomePage = false,
            ModifiedAt =
                fields.TryGetValue("Modified", out SourceField? modified) && modified.ToValue() is DateTime date
                    ? date
                    : default,
            ModifiedBy = FilePageProjection.ModifiedBy(fields, options.SkipUserInformation),
            RemediationCode = fileBacked
                ? null
                : pageType == ClassicPageRules.BlogPage
                    ? "CP4"
                    : "CP5",
        };
    }
}