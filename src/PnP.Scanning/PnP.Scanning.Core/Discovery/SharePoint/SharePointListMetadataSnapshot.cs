using System.Globalization;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// A complete, Web-bound discovery projection of the Lists expansion in a raw SDK Web response.
/// An instantiated SDK collection alone does not prove that its enumeration was complete.
/// </summary>
internal sealed class SharePointListMetadataSnapshot
{
    private static readonly HashSet<string> ProjectedFields = new(StringComparer.Ordinal)
    {
        "Id", "Title", "BaseType", "BaseTemplate", "Hidden", "IsCatalog",
        "RootFolder/ServerRelativeUrl", "RootFolder/UniqueId", "DefaultViewUrl", "DefaultDisplayFormUrl",
        "ContentTypes/Id", "ContentTypes/DisplayFormUrl",
    };

    private SharePointListMetadataSnapshot(Uri webUrl, IReadOnlyList<JsonElement> items, DateTimeOffset receivedAtUtc)
    {
        WebUrl = webUrl;
        Items = items;
        ReceivedAtUtc = receivedAtUtc;
    }

    internal Uri WebUrl { get; }
    internal IReadOnlyList<JsonElement> Items { get; }
    internal DateTimeOffset ReceivedAtUtc { get; }

    // This describes the fixed projection, not the rich payload or the presence of any rows.
    // A future discovery selector must use raw authority if it needs columns we did not retain.
    internal bool SupportsFields(string select) => !string.IsNullOrWhiteSpace(select) &&
        select.Split(',').All(field => ProjectedFields.Contains(field.Trim()));

    internal static SharePointListMetadataSnapshot TryCreate(Uri webUrl, string json,
        DateTimeOffset? receivedAtUtc = null)
    {
        if (webUrl == null || !webUrl.IsAbsoluteUri || webUrl.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(webUrl.UserInfo) || !string.IsNullOrEmpty(webUrl.Query) ||
            !string.IsNullOrEmpty(webUrl.Fragment) || string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var web = document.RootElement;
            if (!HasUnambiguousTerminalShape(web) || web.ValueKind != JsonValueKind.Object ||
                web.TryGetProperty("error", out _) || web.TryGetProperty("odata.error", out _)) return null;
            if (web.TryGetProperty("d", out _) && web.TryGetProperty("value", out _)) return null;
            if (web.TryGetProperty("Lists", out _) &&
                (web.TryGetProperty("d", out _) || web.TryGetProperty("value", out _))) return null;
            if (web.TryGetProperty("d", out var verbose)) web = verbose;
            else if (web.TryGetProperty("value", out var value)) web = value;
            if (web.ValueKind != JsonValueKind.Object || web.TryGetProperty("error", out _) ||
                web.TryGetProperty("odata.error", out _) || !web.TryGetProperty("Lists", out var lists)) return null;

            var collection = lists;
            if (lists.ValueKind == JsonValueKind.Object)
            {
                if (lists.TryGetProperty("results", out var results)) collection = results;
                else if (lists.TryGetProperty("value", out var values)) collection = values;
                else return null;
                if (lists.TryGetProperty("results", out _) && lists.TryGetProperty("value", out _)) return null;
            }
            if (collection.ValueKind != JsonValueKind.Array ||
                !CountMatches(lists, "__count", collection.GetArrayLength()) ||
                !CountMatches(lists, "odata.count", collection.GetArrayLength()) ||
                !CountMatches(lists, "@odata.count", collection.GetArrayLength()) ||
                !CountMatches(web, "Lists@odata.count", collection.GetArrayLength())) return null;

            var items = new List<JsonElement>();
            var listIds = new HashSet<Guid>();
            foreach (var item in collection.EnumerateArray())
            {
                // SDK models merge rows by ID; duplicates cannot prove an accurate authority count.
                if (!ValidList(item) || !listIds.Add(Guid.Parse(item.GetProperty("Id").GetString()))) return null;
                // Retain only discovery columns, including the content-type form routing projection.
                var projection = new Dictionary<string, object>
                {
                    ["Id"] = item.GetProperty("Id"),
                    ["Title"] = item.GetProperty("Title"),
                    ["BaseType"] = item.GetProperty("BaseType"),
                    ["BaseTemplate"] = item.GetProperty("BaseTemplate"),
                    ["Hidden"] = item.GetProperty("Hidden"),
                    ["IsCatalog"] = item.GetProperty("IsCatalog"),
                    ["RootFolder"] = new Dictionary<string, JsonElement>
                    {
                        ["ServerRelativeUrl"] = item.GetProperty("RootFolder").GetProperty("ServerRelativeUrl"),
                        ["UniqueId"] = item.GetProperty("RootFolder").GetProperty("UniqueId"),
                    },
                    ["DefaultViewUrl"] = item.GetProperty("DefaultViewUrl"),
                    ["DefaultDisplayFormUrl"] = item.GetProperty("DefaultDisplayFormUrl"),
                    ["ContentTypes"] = ContentTypes(item).EnumerateArray().Select(contentType => new Dictionary<string, JsonElement>
                    {
                        ["Id"] = contentType.GetProperty("Id"),
                        ["DisplayFormUrl"] = contentType.GetProperty("DisplayFormUrl"),
                    }).ToArray(),
                };
                items.Add(JsonSerializer.SerializeToElement(projection));
            }

            return new SharePointListMetadataSnapshot(new Uri(webUrl.AbsoluteUri.TrimEnd('/')),
                items.AsReadOnly(), (receivedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ValidList(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && ValidGuid(item, "Id") &&
        String(item, "Title") && Integer(item, "BaseType") && Integer(item, "BaseTemplate") &&
        Boolean(item, "Hidden") && Boolean(item, "IsCatalog") &&
        item.TryGetProperty("RootFolder", out var folder) && folder.ValueKind == JsonValueKind.Object &&
        String(folder, "ServerRelativeUrl") &&
        !string.IsNullOrWhiteSpace(folder.GetProperty("ServerRelativeUrl").GetString()) &&
        folder.GetProperty("ServerRelativeUrl").GetString().StartsWith('/') && ValidGuid(folder, "UniqueId") &&
        NullableString(item, "DefaultViewUrl") && NullableString(item, "DefaultDisplayFormUrl") &&
        ValidContentTypes(item);

    private static bool ValidContentTypes(JsonElement item)
    {
        var contentTypes = ContentTypes(item);
        if (contentTypes.ValueKind != JsonValueKind.Array) return false;
        var count = contentTypes.GetArrayLength();
        var wrapper = item.GetProperty("ContentTypes");
        if (!CountMatches(wrapper, "__count", count) || !CountMatches(wrapper, "odata.count", count) ||
            !CountMatches(wrapper, "@odata.count", count) || !CountMatches(item, "ContentTypes@odata.count", count)) return false;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contentType in contentTypes.EnumerateArray())
        {
            if (contentType.ValueKind != JsonValueKind.Object || !contentType.TryGetProperty("Id", out var id) ||
                !NullableString(contentType, "DisplayFormUrl")) return false;
            // Preserve REST's SP.ContentTypeId object instead of flattening away StringValue.
            if (id.ValueKind == JsonValueKind.Object && !id.TryGetProperty("StringValue", out id)) return false;
            if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) ||
                !ids.Add(id.GetString())) return false;
        }
        return true;
    }

    private static JsonElement ContentTypes(JsonElement item)
    {
        if (!item.TryGetProperty("ContentTypes", out var contentTypes)) return default;
        if (contentTypes.ValueKind != JsonValueKind.Object) return contentTypes;
        var hasResults = contentTypes.TryGetProperty("results", out var results);
        var hasValue = contentTypes.TryGetProperty("value", out var value);
        return hasResults == hasValue ? default : hasResults ? results : value;
    }

    private static bool NullableString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Null;

    private static bool ValidGuid(JsonElement item, string name) =>
        String(item, name) && Guid.TryParse(item.GetProperty(name).GetString(), out var id) && id != Guid.Empty;

    private static bool String(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String;

    private static bool Integer(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);

    private static bool Boolean(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool CountMatches(JsonElement item, string name, int count)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value)) return true;
        return value.ValueKind == JsonValueKind.Number ? value.TryGetInt32(out var number) && number == count
            : value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var text) && text == count;
    }

    private static bool HasUnambiguousTerminalShape(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name;
                // Expanded child collections may carry their continuation on the parent object.
                // Reject these even for children not retained in the discovery projection.
                if (!names.Add(name) || name.Equals("__next", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("nextLink", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("NextHref", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("continuation", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("nextPageToken", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("skipToken", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("hasMore", StringComparison.OrdinalIgnoreCase) ||
                    !HasUnambiguousTerminalShape(property.Value)) return false;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (!HasUnambiguousTerminalShape(item)) return false;
        }
        return true;
    }
}
