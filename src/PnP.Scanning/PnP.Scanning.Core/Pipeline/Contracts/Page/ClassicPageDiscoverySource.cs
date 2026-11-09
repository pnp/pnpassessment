#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

/// <summary>
/// Groups captured discovery rows and physical file facts under one Web owner.
/// </summary>
internal sealed record ClassicPageDiscoverySource(string SiteUrl, string WebUrl, ClassicPageDiscoveryRow[] Rows,
    SPFileSource[]? Files = null);
