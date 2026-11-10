#nullable enable
using System.Globalization;
using PnP.Scanning.Core.ClassicPages.Contracts.List;
using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using PnP.Scanning.Core.Pipeline.Contracts.Module;

namespace PnP.Scanning.Core.ClassicPages.Analysis.Page;

/// <summary>Projects real Posts items into native Pages without assigning their renderer's physical identity.</summary>
internal static class ListItemPageProjection
{
    /// <summary>Requires observed Posts ownership and item identity, retaining an unknown locator when no renderer was acquired.</summary>
    internal static ClassicPageRow? Analyze(
        Guid assessmentId,
        ListItemBatch batch,
        ListItemFacts item,
        WebFacts web,
        ClassicCollectionOptions options)
    {
        if (options.HomePageOnly) return null;
        if (batch.QueryKind != ListItemQueryKind.Posts || batch.ListTemplate != 301)
            throw new SnapshotIntegrityException(
                "Independent content projection requires an observed Posts list template.");
        if (batch.ListId == Guid.Empty || item.ListItemId <= 0 || item.Fields.GetValueOrDefault("ID")?.Text !=
            item.ListItemId.ToString(CultureInfo.InvariantCulture))
            throw new SnapshotIntegrityException("Content projection requires the returned native item identity.");
        Dictionary<string, SourceField> fields = item.Fields;
        string contentType = fields.GetValueOrDefault("ContentTypeId")?.Text ?? "";
        string? renderer = batch.ContentTypeForms?.OrderByDescending(form => form.ContentTypeId.Length)
            .FirstOrDefault(form => contentType.StartsWith(form.ContentTypeId, StringComparison.OrdinalIgnoreCase))
            ?.FormUrl ?? batch.DisplayFormUrl;
        if (renderer != null && (!renderer.StartsWith('/') || renderer.StartsWith("//", StringComparison.Ordinal)))
            throw new SnapshotIntegrityException("The captured renderer is not a normalized declaration.");
        string? locator = renderer == null
            ? null
            : renderer + (renderer.Contains('?') ? "&" : "?") + "ID=" +
              item.ListItemId.ToString(CultureInfo.InvariantCulture);
        return new ClassicPageRow
        {
            Identity = PageIdentity.Item(web.SiteCollectionId, batch.ListId, item.ListItemId),
            ScanId = assessmentId,
            SiteCollectionId = web.SiteCollectionId,
            WebId = web.WebId,
            SiteUrl = batch.SiteUrl,
            WebUrl = batch.WebUrl,
            ListId = batch.ListId,
            ListItemId = item.ListItemId,
            ListTitle = batch.ListTitle,
            ListUrl = batch.ListUrl,
            ListTemplate = batch.ListTemplate,
            PageUrl = locator!,
            PageName = fields.GetValueOrDefault("Title")?.Text ?? "",
            PageType = ClassicPageRules.BlogPage,
            AssessmentPageType = ClassicPageRules.BlogPage,
            SourceKind = "ListItem",
            ContentReadState = "NotApplicable",
            ClassificationReason = "ObservedListItemAndRendererDeclaration",
            DiscoveryStatus = "Discovered",
            AssessmentStatus = locator == null ? "Unknown" : "Complete",
            HomePage = false,
            ModifiedAt =
                fields.TryGetValue("Modified", out SourceField? modified) && modified.ToValue() is DateTime date
                    ? date
                    : default,
            ModifiedBy = FilePageProjection.ModifiedBy(fields, options.SkipUserInformation),
            RemediationCode = "CP4",
        };
    }
}