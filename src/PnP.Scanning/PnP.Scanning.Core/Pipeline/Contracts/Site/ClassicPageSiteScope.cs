#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Page;

namespace PnP.Scanning.Core.Pipeline.Contracts.Site;

/// <summary>
/// Retains one site's fixed Web identities, templates, and enumeration evidence.
/// </summary>
internal sealed record ClassicPageSiteScope(
    string SiteUrl,
    string[] WebUrls,
    string[] Templates,
    SourceReadState State,
    ClassicPageDiscoveryRow[]? Evidence = null);