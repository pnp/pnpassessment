#nullable enable
using System.Globalization;
using System.Text.Json;


namespace PnP.Scanning.Core.Pipeline.Contracts;

internal sealed record SourceReadState(string Status, string? Error = null)
{
    internal static readonly SourceReadState Complete = new("Complete");
    internal static readonly SourceReadState NotAttempted = new("NotAttempted");
    internal bool Succeeded => Status == "Complete";
}

/// <summary>Preserves field type, explicit null and absence without retaining an SDK value.</summary>
internal sealed record SourceField(string Type, JsonElement Value)
{
    internal static SourceField Of(string type, object? value) => new(type, JsonSerializer.SerializeToElement(value));
    internal string? Text => Value.ValueKind == JsonValueKind.Null ? null : Value.ValueKind == JsonValueKind.String ? Value.GetString() : Value.ToString();
    internal object? ToValue() => Value.ValueKind == JsonValueKind.Null ? null : Type switch
    {
        "DateTime" => DateTime.Parse(Text!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        "Guid" => Guid.Parse(Text!),
        "Int32" => Value.GetInt32(),
        "Boolean" => Value.GetBoolean(),
        _ => Text,
    };
}

internal sealed record ClassicPageSourceOptions(bool ExportWebPartProperties, bool SkipUsageInformation,
    bool SkipUserInformation, bool HomePageOnly, int AuditLogWindowDays);
internal sealed record ClassicPageSiteScope(string SiteUrl, string[] WebUrls, string[] Templates, SourceReadState State);
internal sealed record ClassicPageScopeSource(string[] Sites, ClassicPageSiteScope[] SiteScopes,
    bool ExplicitSites, ClassicPageSourceOptions Options, DateTime AuditWindowStart, DateTime AuditWindowEnd,
    ClassicPageDiscoveryRow[] Evidence);
internal sealed record ClassicPageWebSource(string SiteUrl, string WebUrl, string Template, Guid SiteId, Guid WebId,
    DateTime LastItemUserModifiedDate, Guid[] SiteFeatures, Guid[] WebFeatures, string? WelcomePage,
    SourceReadState WelcomePageState, bool? CanModernizeHomepage, SourceReadState CanModernizeState,
    string? MasterUrl, int Language, string? LocalizedHomePageResource, SourceReadState HomeFallbackState,
    SourceReadState State);
internal sealed record ClassicPageDiscoverySource(string SiteUrl, string WebUrl, ClassicPageDiscoveryRow[] Rows);
internal sealed record ClassicPageWebPartSource(Guid Id, string? ControlId, string ZoneId, int ZoneIndex,
    string? Title, bool Hidden, bool IsClosed, string ExportMode, Dictionary<string, SourceField> Properties,
    string? ExportXml, SourceReadState ExportState);
internal sealed record ClassicPageItemSource(string SiteUrl, string WebUrl, string DiscoveryKey,
    Guid? ListId, string? ListTitle, string? ListUrl, Dictionary<string, SourceField> Fields,
    Dictionary<string, SourceField> FileProperties, ClassicPageWebPartSource[] WebParts,
    SourceReadState MetadataState, SourceReadState WebPartsState, string? ContentTypeDisplayFormTemplateName,
    SourceReadState HomeFallbackState);
internal sealed record ClassicPageBlogBatchSource(string SiteUrl, string WebUrl, Guid ListId, string ListTitle,
    string ListUrl, Dictionary<string, SourceField>[] Items, string? NextPage, SourceReadState State);
internal sealed record ClassicPageAuditPageSource(int Chunk, string QueryId, int Page, string Body, string? NextLink,
    int StatusCode = 200, string? Error = null);
internal sealed record ClassicPageAuditChunkSource(int Chunk, DateTime Start, DateTime End,
    string? QueryId, string Status, string? Error);

internal static class ClassicPageSourceJson
{
    internal static T Convert<T>(object value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    internal static byte[] Bytes<T>(T value) => System.Text.Encoding.UTF8.GetBytes(VersionedJson.From(value).Json);
    internal static T Read<T>(byte[]? bytes) => bytes == null
        ? throw new SnapshotIntegrityException("A required Classic Page source artifact was not acquired.")
        : new VersionedJson(System.Text.Encoding.UTF8.GetString(bytes)).Value.Deserialize<T>() ?? throw new SnapshotIntegrityException("Classic Page source artifact is empty.");
    internal static string Kind(SourceRecord record) => record.Metadata.Value.GetProperty("kind").GetString()!;
    internal static string Kind(CollectedObservation record) => record.Metadata.Value.GetProperty("kind").GetString()!;
    internal static string WebKey(string siteUrl, string webUrl) => siteUrl.TrimEnd('/').ToLowerInvariant() + "|" + webUrl.ToLowerInvariant();
}
