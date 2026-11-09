#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Site;

internal sealed record ClassicPageSiteScope(string SiteUrl, string[] WebUrls, string[] Templates, SourceReadState State);
