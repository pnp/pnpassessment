#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Site;

namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

/// <summary>
/// Fixes selected sites, web enumeration, collection options, audit interval and scope evidence for offline analysis.
/// </summary>
internal sealed record ClassicPageScopeSource(string[] Sites, ClassicPageSiteScope[] SiteScopes,
    bool ExplicitSites, ClassicPageSourceOptions Options, DateTime AuditWindowStart, DateTime AuditWindowEnd,
    ClassicPageDiscoveryRow[] Evidence);
