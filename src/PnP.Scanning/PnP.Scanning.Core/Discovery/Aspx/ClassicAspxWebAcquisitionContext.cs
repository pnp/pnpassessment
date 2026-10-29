using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Discovery.Aspx;

/// <summary>Observed site/Web identity and a finite publication bound for physical ASPX inventory projection.</summary>
internal sealed record ClassicAspxWebAcquisitionContext(
    Guid ScanId, Guid SiteCollectionId, Guid WebId, Uri SiteUrl, Uri WebUrl, int BatchSize = 128);

/// <summary>A bounded batch of physical inventory observations and their projection validation gaps.</summary>
internal sealed record ClassicAspxAcquisitionBatch(
    IReadOnlyList<ClassicAspxInventory> Inventory,
    IReadOnlyList<ClassicPageDiscovery> Evidence);
