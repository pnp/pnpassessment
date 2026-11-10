using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;

namespace PnP.Scanning.Core.Pipeline.Collection.List;

/// <summary>
/// Acquires paged content items using isolated SDK contexts and captured list scope.
/// </summary>
internal static class ListItemContentReader
{
    /// <summary>
    /// Captures candidate native list scopes and display-form declarations using a cloned SDK context.
    /// </summary>
    internal static async Task<WebListItemQueries> ReadListsAsync(PnPContext pnp, WebFacts web, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // List reloads replace SDK metadata needed by the other mixed Classic components.
        using PnPContext? contentContext = await pnp.CloneAsync();
        await contentContext.Web.LoadAsync(w => w.Lists.QueryProperties(
            l => l.Id,
            l => l.Title,
            l => l.TemplateType,
            l => l.BaseType,
            l => l.DefaultDisplayFormUrl,
            l => l.ContentTypes.QueryProperties(type => type.StringId, type => type.DisplayFormUrl),
            l => l.RootFolder.QueryProperties(f => f.ServerRelativeUrl)));
        token.ThrowIfCancellationRequested();
        ListItemQueryScope[]? lists = contentContext.Web.Lists.AsRequested()
            .Select(l => new ListItemQueryScope(
                l.Id,
                l.Title,
                l.RootFolder.ServerRelativeUrl,
                l.BaseType == ListBaseType.DocumentLibrary
                    ? ListItemQueryKind.PointPublishingFiles
                    : l.TemplateType == ListTemplateType.Posts
                        ? ListItemQueryKind.Posts
                        : ListItemQueryKind.CustomDisplayForms,
                (int)l.TemplateType,
                l.BaseType == ListBaseType.DocumentLibrary
                    ? null
                    : NormalizeDisplayForm(l.DefaultDisplayFormUrl, pnp.Uri, web.SiteUrl),
                l.BaseType == ListBaseType.DocumentLibrary
                    ? null
                    : l.ContentTypes.AsRequested()
                        .Select(type => new ClassicContentTypeForm(
                            type.StringId,
                            NormalizeDisplayForm(type.DisplayFormUrl, pnp.Uri, web.SiteUrl)!))
                        .Where(type => type.FormUrl != null)
                        .OrderByDescending(type => type.ContentTypeId.Length)
                        .ToArray()))
            .Where(list => list.QueryKind != ListItemQueryKind.CustomDisplayForms ||
                           LegacyDisplayFormAssessment.IsContentPageForm(list.DisplayFormUrl) ||
                           list.ContentTypeForms?.Any(type =>
                               LegacyDisplayFormAssessment.IsContentPageForm(type.FormUrl)) == true)
            .OrderBy(l => l.ListId)
            .ToArray();
        return new(web.SiteUrl, web.WebUrl, lists, SourceReadState.Complete);
    }

    /// <summary>
    /// Normalizes a captured form declaration and rejects foreign authorities.
    /// </summary>
    private static string? NormalizeDisplayForm(string? value, Uri web, string siteUrl)
    {
        Uri? site = new Uri(siteUrl);
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? absolute) &&
            !SharePointAspxUrlPolicy.HasSameHttpsAuthority(site, absolute))
        {
            Serilog.Log.Warning(
                "A content display-form declaration has a foreign authority; its content URL remains unknown");
            return null;
        }

        return DisplayFormUrlPolicy.ToServerRelative(value!, web.AbsolutePath, site.AbsolutePath);
    }

    /// <summary>
    /// Validates the selected native list and acquires one bounded content-item page with its continuation.
    /// </summary>
    internal static async Task<ListItemBatch> ReadBatchAsync(
        PnPContext pnp,
        WebFacts web,
        ListItemQueryScope expected,
        string? paging,
        bool skipUsers,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using PnPContext? contentContext = await pnp.CloneAsync();
        IList? list = await contentContext.Web.Lists.GetByIdAsync(
            expected.ListId,
            l => l.Id,
            l => l.Title,
            l => l.TemplateType,
            l => l.BaseType,
            l => l.RootFolder.QueryProperties(f => f.ServerRelativeUrl));
        token.ThrowIfCancellationRequested();
        if (!string.Equals(list.RootFolder.ServerRelativeUrl, expected.ListUrl, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected native list root changed after query-scope acquisition.");
        bool pointPublishingQuery = expected.QueryKind == ListItemQueryKind.PointPublishingFiles;
        if ((int)list.TemplateType != expected.ListTemplate ||
            (!pointPublishingQuery && expected.QueryKind == ListItemQueryKind.Posts &&
             list.TemplateType != ListTemplateType.Posts) ||
            (!pointPublishingQuery && list.BaseType == ListBaseType.DocumentLibrary) ||
            (pointPublishingQuery && list.BaseType != ListBaseType.DocumentLibrary))
            throw new InvalidDataException("The observed list template or query kind differs from its fixed scope.");
        string query = ListItemQueryBuilder.Create(
            new(),
            filterOnAspxFiles: false,
            skipUserInformation: skipUsers,
            filterOnPointPublishingFiles: pointPublishingQuery);
        ListItemFieldBatch batch = await ClassicContentReader.LoadItemFieldsAsync(list, query, paging, token);
        return new ListItemBatch(
            web.SiteUrl,
            web.WebUrl,
            batch.ListId,
            batch.ListTitle,
            batch.ListUrl,
            batch.Items.Select(fields => new ListItemFacts(ParseItemId(fields), fields)).ToArray(),
            batch.NextPage,
            batch.State,
            expected.QueryKind,
            (int)list.TemplateType,
            expected.DisplayFormUrl,
            ContentTypeForms: expected.ContentTypeForms);
    }

    /// <summary>
    /// Requires the real returned native item ID before an acquisition receipt can represent a ListItem.
    /// </summary>
    private static int ParseItemId(Dictionary<string, SourceField> fields) =>
        int.TryParse(
            fields.GetValueOrDefault("ID")?.Text,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out int itemId) && itemId > 0
            ? itemId
            : throw new InvalidDataException("The list response did not contain a valid native item ID.");
}