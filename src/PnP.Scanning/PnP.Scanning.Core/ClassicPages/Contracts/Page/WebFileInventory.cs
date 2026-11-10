#nullable enable

namespace PnP.Scanning.Core.ClassicPages.Contracts.Page;

/// <summary>
/// Groups captured discovery rows and physical file facts under one Web owner.
/// </summary>
internal sealed record WebFileInventory(
    string SiteUrl,
    string WebUrl,
    DiscoveryEvidence[] Evidence,
    PhysicalFileFacts[]? Files = null);