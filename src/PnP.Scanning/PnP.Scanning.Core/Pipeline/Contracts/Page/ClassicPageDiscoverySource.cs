#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

/// <summary>
/// Carries a web-owned set of physical page identities, traversal evidence and discovery gaps collected for the snapshot.
/// </summary>
internal sealed record ClassicPageDiscoverySource(string SiteUrl, string WebUrl, ClassicPageDiscoveryRow[] Rows);
