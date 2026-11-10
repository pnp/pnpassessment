#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Web;

namespace PnP.Scanning.Core.Pipeline.Contracts.Site;

/// <summary>
/// Retains one site's fixed Web identities, templates, and enumeration evidence.
/// </summary>
internal sealed record SiteCollectionScope(
    string SiteUrl,
    WebScope[] Webs,
    SourceReadState State,
    DiscoveryEvidence[]? Evidence = null);