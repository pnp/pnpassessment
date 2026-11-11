using SdkList = PnP.Core.Model.SharePoint.IList;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.Discovery;
#nullable enable
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.ClassicPages.Contracts.List;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;

namespace PnP.Scanning.Core.ClassicPages.Collection.List;

/// <summary>
/// Acquires independent Posts items using isolated SDK contexts and captured native list scope.
/// </summary>
internal static class ListItemContentReader
{
    /// <summary>
    /// Selects real Posts lists and retains their renderer declarations without enumerating other business items.
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
            l => l.DefaultDisplayFormUrl,
            l => l.ContentTypes.QueryProperties(type => type.StringId, type => type.DisplayFormUrl),
            l => l.RootFolder.QueryProperties(f => f.ServerRelativeUrl)));
        token.ThrowIfCancellationRequested();
        ListItemQueryScope[]? lists = contentContext.Web.Lists.AsRequested()
            .Where(l => IsPostsTemplate((int)l.TemplateType))
            .Select(l => new ListItemQueryScope(
                l.Id,
                l.Title,
                l.RootFolder.ServerRelativeUrl,
                ListItemQueryKind.Posts,
                (int)l.TemplateType,
                NormalizeDisplayForm(l.DefaultDisplayFormUrl, pnp.Uri, web.SiteUrl),
                l.ContentTypes.AsRequested()
                    .Select(type => new ClassicContentTypeForm(
                        type.StringId,
                        NormalizeDisplayForm(type.DisplayFormUrl, pnp.Uri, web.SiteUrl)!))
                    .Where(type => type.FormUrl != null)
                    .OrderByDescending(type => type.ContentTypeId.Length)
                    .ToArray()))
            .OrderBy(l => l.ListId)
            .ToArray();
        return new(web.SiteUrl, web.WebUrl, lists, SourceReadState.Complete);
    }

    /// <summary>Admits independent content only from the actual SharePoint Posts template, never a URL or content-type guess.</summary>
    internal static bool IsPostsTemplate(int template) => template == (int)ListTemplateType.Posts;

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
        if (!IsPostsTemplate((int)list.TemplateType) || (int)list.TemplateType != expected.ListTemplate ||
            expected.QueryKind != ListItemQueryKind.Posts)
            throw new InvalidDataException("The observed list template or query kind differs from its fixed scope.");
        string query = ListItemQueryBuilder.ForPosts(skipUsers);
        ListItemFieldBatch batch = await ListItemContentReader.LoadItemFieldsAsync(list, query, paging, token);
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

    /// <summary>
    /// Loads a bounded native list-stream page and detaches its returned typed items and continuation.
    /// </summary>
    internal static async Task<ListItemFieldBatch> LoadItemFieldsAsync(
        SdkList list,
        string query,
        string? paging,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        list.Items.Clear();
        Dictionary<string, object>? response = await list.LoadListDataAsStreamAsync(
            new PnP.Core.Model.SharePoint.RenderListDataOptions
            {
                ViewXml = query, RenderOptions = RenderListDataOptionsFlags.ListData, Paging = paging
            });
        token.ThrowIfCancellationRequested();
        return new(
            list.Id,
            list.Title,
            list.RootFolder.ServerRelativeUrl,
            list.Items.AsRequested().Select(ItemFields).ToArray(),
            response.TryGetValue("NextHref", out object? next) ? next?.ToString()?.TrimStart('?') : null,
            SourceReadState.Complete);
    }

    /// <summary>
    /// Converts a captured property bag into detached typed source fields.
    /// </summary>
    internal static Dictionary<string, SourceField> Fields(IDictionary<string, object> fields) =>
        fields.ToDictionary(x => x.Key, x => Field(x.Value), StringComparer.Ordinal);

    /// <summary>
    /// Captures returned fields and validates the modeled native item ID.
    /// </summary>
    internal static Dictionary<string, SourceField> ItemFields(IListItem item)
    {
        Dictionary<string, SourceField>? fields = Fields(item.Values);
        // RenderListDataAsStream puts ID in the modeled Id property and deliberately omits it
        // from Values. Persist that returned identity along with the detached field values.
        if (fields.TryGetValue("ID", out SourceField? existing) && existing.ToValue() is int fieldId &&
            fieldId != item.Id)
            throw new InvalidDataException("The list item's modeled ID differs from its returned ID field.");
        fields["ID"] = SourceField.Of("Int32", item.Id);
        return fields;
    }

    /// <summary>
    /// Preserves a supported captured field type and rejects unsupported SDK value kinds.
    /// </summary>
    private static SourceField Field(object? value) =>
        value switch
        {
            null => SourceField.Of("Null", null),
            IFieldUserValue user => LookupField(user),
            IFieldLookupValue lookup => LookupField(lookup),
            Microsoft.SharePoint.Client.FieldUserValue user => SourceField.Of(
                "User",
                new { Email = user.Email, user.LookupValue, user.LookupId }),
            Microsoft.SharePoint.Client.FieldUrlValue url => SourceField.Of("Url", new { url.Url, url.Description }),
            IFieldUrlValue url => SourceField.Of("Url", new { url.Url, url.Description }),
            DateTime date => SourceField.Of("DateTime", date),
            Guid guid => SourceField.Of("Guid", guid),
            int number => SourceField.Of("Int32", number),
            uint number => SourceField.Of("Int64", (long)number),
            long number => SourceField.Of("Int64", number),
            double number => SourceField.Of("Double", number),
            decimal number => SourceField.Of("Decimal", number),
            bool boolean => SourceField.Of("Boolean", boolean),
            string text => SourceField.Of("String", text),
            List<string> values => SourceField.Of("Json", values),
            System.Text.Json.JsonElement json => SourceField.Of("Json", json),
            _ => throw new NotSupportedException($"Unsupported source field type '{value.GetType().FullName}'."),
        };

    /// <summary>Detaches available lookup/user properties without treating an omitted SDK display value as failure of the acquired item.</summary>
    private static SourceField LookupField(IFieldLookupValue lookup)
    {
        Dictionary<string, object?> values = new();
        if (lookup is IFieldUserValue user) CaptureProperty(values, nameof(user.Email), () => user.Email);
        CaptureProperty(values, nameof(lookup.LookupValue), () => lookup.LookupValue);
        // The SDK returns -1 when LookupId was not loaded; it is not an observed native identity.
        if (lookup.LookupId >= 0) values[nameof(lookup.LookupId)] = lookup.LookupId;
        return SourceField.Of(lookup is IFieldUserValue ? "User" : "Lookup", values);
    }

    /// <summary>Retains explicit null or text and omits only a specifically unacquired SDK property; other getter errors remain failures.</summary>
    private static void CaptureProperty(Dictionary<string, object?> values, string name, Func<string?> read)
    {
        try
        {
            values.Add(name, read());
        }
        catch (PnP.Core.ClientException error) when (error.Error.Type == PnP.Core.ErrorType.PropertyNotLoaded)
        {
            Serilog.Log.Debug("Captured list-field property {Property} was not returned by the SDK", name);
        }
    }
}