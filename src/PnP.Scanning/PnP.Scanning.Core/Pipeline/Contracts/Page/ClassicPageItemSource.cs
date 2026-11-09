#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

/// <summary>
/// Preserves typed item fields, web-part exports, native identity, and independent read states.
/// </summary>
internal sealed record ClassicPageItemSource(string SiteUrl, string WebUrl, string DiscoveryKey,
    Guid? ListId, string? ListTitle, string? ListUrl, Dictionary<string, SourceField> Fields,
    Dictionary<string, SourceField> FileProperties, ClassicPageWebPartSource[] WebParts,
    SourceReadState MetadataState, SourceReadState WebPartsState, string? ContentTypeDisplayFormTemplateName,
    SourceReadState HomeFallbackState, SPFileSource? FileFacts = null, bool FileLevelOnly = false,
    string[]? ListRootFolderUrls = null, bool? OwningListMatched = null, ClassicPublishingLayoutSource? PublishingLayout = null,
    Dictionary<string, SourceField>[]? ReturnedItems = null);
