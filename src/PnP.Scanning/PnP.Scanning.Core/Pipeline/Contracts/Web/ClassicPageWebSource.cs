#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Web;

internal sealed record ClassicPageWebSource(string SiteUrl, string WebUrl, string Template, Guid SiteId, Guid WebId,
    DateTime LastItemUserModifiedDate, Guid[] SiteFeatures, Guid[] WebFeatures, string? WelcomePage,
    SourceReadState WelcomePageState, bool? CanModernizeHomepage, SourceReadState CanModernizeState,
    string? MasterUrl, int Language, string? LocalizedHomePageResource, SourceReadState HomeFallbackState,
    SourceReadState State);
