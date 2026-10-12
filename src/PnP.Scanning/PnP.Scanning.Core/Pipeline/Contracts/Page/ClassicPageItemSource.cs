#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

/// <summary>
/// Preserves a discovered page identity, typed fields, web part exports and independent metadata and enrichment read states.
/// <para>Evolved from <c>Scanners/Classic/PageScanComponent.cs</c> to replace live page enrichment inputs with persistable field values and read states.</para>
/// </summary>
internal sealed record ClassicPageItemSource(string SiteUrl, string WebUrl, string DiscoveryKey,
    Guid? ListId, string? ListTitle, string? ListUrl, Dictionary<string, SourceField> Fields,
    Dictionary<string, SourceField> FileProperties, ClassicPageWebPartSource[] WebParts,
    SourceReadState MetadataState, SourceReadState WebPartsState, string? ContentTypeDisplayFormTemplateName,
    SourceReadState HomeFallbackState);
