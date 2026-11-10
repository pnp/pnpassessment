#nullable enable
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Site;

namespace PnP.Scanning.Core.ClassicPages.Contracts.Module;

/// <summary>
/// Freezes selected sites, Web enumeration, options, audit window, and scope evidence.
/// </summary>
internal sealed record ClassicCollectionScope(
    string[] SiteCollectionUrls,
    SiteCollectionScope[] SiteCollections,
    bool ExplicitSites,
    ClassicCollectionOptions Options,
    DateTime AuditWindowStart,
    DateTime AuditWindowEnd,
    DiscoveryEvidence[] Evidence,
    bool ClassicComponentsRequested = false);