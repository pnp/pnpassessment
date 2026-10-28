using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Discovery.M1;

internal sealed record M1WebAcquisitionContext(
    Guid ScanId, Guid SiteCollectionId, Guid WebId, Uri SiteUrl, Uri WebUrl,
    ClassicOptions Options, PlatformAspxCatalog Catalog = null,
    int BatchSize = 128, int MaxCollectionPages = 10000, int MaxFileBytes = 2097152,
    TimeSpan? FileReadTimeout = null, string AuthorityRevision = "M1/v1",
    int MaxContentTypes = 4096, int MaxResourceCacheEntries = 2048);

/// <summary>Bounded incremental observations, not a complete-universe in-memory projection.</summary>
internal sealed record M1AcquisitionBatch(
    IReadOnlyList<ClassicAspxInventory> Inventory,
    IReadOnlyList<ClassicAspxItemBinding> Bindings,
    IReadOnlyList<ClassicPageDiscovery> Evidence);
