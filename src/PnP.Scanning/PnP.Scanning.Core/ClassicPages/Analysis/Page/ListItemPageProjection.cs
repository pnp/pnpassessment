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
    /// <summary>
    /// Requires observed Posts ownership and item identity. When no renderer was acquired the item is addressed through
    /// SharePoint's list form redirect, so every post keeps an address and a report row.
    /// </summary>
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
        string itemId = item.ListItemId.ToString(CultureInfo.InvariantCulture);
        string locator = renderer == null
            ? ListFormRedirect(batch.SiteUrl, batch.WebUrl, batch.ListId, itemId)
            : renderer + (renderer.Contains('?') ? "&" : "?") + "ID=" + itemId;
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
            PageUrl = locator,
            PageName = fields.GetValueOrDefault("Title")?.Text ?? "",
            PageType = ClassicPageRules.BlogPage,
            AssessmentPageType = ClassicPageRules.BlogPage,
            SourceKind = "ListItem",
            ContentReadState = "NotApplicable",
            ClassificationReason = renderer == null
                ? "ObservedListItemAndListFormRedirect"
                : "ObservedListItemAndRendererDeclaration",
            DiscoveryStatus = "Discovered",
            AssessmentStatus = "Complete",
            HomePage = false,
            ModifiedAt =
                fields.TryGetValue("Modified", out SourceField? modified) && modified.ToValue() is DateTime date
                    ? date
                    : default,
            ModifiedBy = FilePageProjection.ModifiedBy(fields, options.SkipUserInformation),
            RemediationCode = "CP4",
        };
    }

    /// <summary>
    /// Builds the server-relative address of SharePoint's list form redirect to one item's display form, from the
    /// observed web, list id and item id only. It is used when the list declares no usable display form.
    /// </summary>
    internal static string ListFormRedirect(string siteUrl, string webUrl, Guid listId, string itemId)
    {
        string web = webUrl.Trim('/');
        return new Uri(siteUrl).AbsolutePath.TrimEnd('/') + (web.Length == 0 ? "" : "/" + web) +
               "/_layouts/15/listform.aspx?PageType=4&ListId=" + listId.ToString("D") + "&ID=" + itemId;
    }
}