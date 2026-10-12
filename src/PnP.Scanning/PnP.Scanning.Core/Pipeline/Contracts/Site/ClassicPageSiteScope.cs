#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Site;

/// <summary>
/// Captures one site collection enumeration result with web URLs, matching templates and an explicit acquisition state.
/// </summary>
internal sealed record ClassicPageSiteScope(string SiteUrl, string[] WebUrls, string[] Templates, SourceReadState State);
