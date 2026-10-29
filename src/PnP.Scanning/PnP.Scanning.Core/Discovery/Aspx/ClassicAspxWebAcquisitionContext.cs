using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Discovery.Aspx;

/// <summary>Immutable identity, declared scope and finite acquisition limits for one native ASPX Web scan.</summary>
internal sealed record ClassicAspxWebAcquisitionContext(
    Guid ScanId, Guid SiteCollectionId, Guid WebId, Uri SiteUrl, Uri WebUrl,
    ClassicOptions Options, PlatformAspxCatalog Catalog = null,
    int BatchSize = 128, int MaxCollectionPages = 10000, int MaxFileBytes = 2097152,
    TimeSpan? FileReadTimeout = null, string AuthorityRevision = ClassicAspxProjectionContract.Version,
    int MaxContentTypes = 4096, int MaxResourceCacheEntries = 2048);

/// <summary>Bounded incremental observations, not a complete-universe in-memory projection.</summary>
internal sealed record ClassicAspxAcquisitionBatch(
    IReadOnlyList<ClassicAspxInventory> Inventory,
    IReadOnlyList<ClassicAspxItemBinding> Bindings,
    IReadOnlyList<ClassicPageDiscovery> Evidence);
