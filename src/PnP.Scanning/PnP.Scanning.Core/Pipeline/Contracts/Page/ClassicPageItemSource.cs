#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

internal sealed record ClassicPageItemSource(string SiteUrl, string WebUrl, string DiscoveryKey,
    Guid? ListId, string? ListTitle, string? ListUrl, Dictionary<string, SourceField> Fields,
    Dictionary<string, SourceField> FileProperties, ClassicPageWebPartSource[] WebParts,
    SourceReadState MetadataState, SourceReadState WebPartsState, string? ContentTypeDisplayFormTemplateName,
    SourceReadState HomeFallbackState);
