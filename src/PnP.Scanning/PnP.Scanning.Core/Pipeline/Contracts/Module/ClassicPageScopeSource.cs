#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Site;

namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

/// <summary>
/// Freezes selected sites, Web enumeration, options, audit window, and scope evidence.
/// </summary>
internal sealed record ClassicPageScopeSource(string[] Sites, ClassicPageSiteScope[] SiteScopes,
    bool ExplicitSites, ClassicPageSourceOptions Options, DateTime AuditWindowStart, DateTime AuditWindowEnd,
    ClassicPageDiscoveryRow[] Evidence, bool ClassicComponentsRequested = false);
