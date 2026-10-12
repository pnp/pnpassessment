#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Web;

/// <summary>
/// Preserves site and web identities, features and home-page facts with separate acquisition states for offline rules.
/// </summary>
internal sealed record ClassicPageWebSource(string SiteUrl, string WebUrl, string Template, Guid SiteId, Guid WebId,
    DateTime LastItemUserModifiedDate, Guid[] SiteFeatures, Guid[] WebFeatures, string? WelcomePage,
    SourceReadState WelcomePageState, bool? CanModernizeHomepage, SourceReadState CanModernizeState,
    string? MasterUrl, int Language, string? LocalizedHomePageResource, SourceReadState HomeFallbackState,
    SourceReadState State);
