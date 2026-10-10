#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

internal sealed record ClassicPageDiscoverySource(string SiteUrl, string WebUrl, ClassicPageDiscoveryRow[] Rows);
