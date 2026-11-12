using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
#nullable enable
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;

namespace PnP.Scanning.Core.ClassicPages.Contracts.Site;

/// <summary>
/// Retains one site's fixed Web identities, templates, and enumeration evidence.
/// </summary>
internal sealed record SiteCollectionScope(
    string SiteUrl,
    WebScope[] Webs,
    SourceReadState State,
    DiscoveryEvidence[]? Evidence = null);