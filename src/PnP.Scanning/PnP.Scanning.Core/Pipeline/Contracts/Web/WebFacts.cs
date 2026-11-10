#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Web;

/// <summary>
/// Preserves native Web identity, features, home-page facts, and independent acquisition states.
/// </summary>
internal sealed record WebFacts(
    string SiteUrl,
    string WebUrl,
    string WebTemplate,
    Guid SiteCollectionId,
    Guid WebId,
    DateTime LastItemUserModifiedDate,
    Guid[] SiteFeatures,
    Guid[] WebFeatures,
    string? WelcomePage,
    SourceReadState WelcomePageState,
    bool? CanModernizeHomepage,
    SourceReadState CanModernizeState,
    string? MasterUrl,
    int Language,
    string? LocalizedHomePageResource,
    SourceReadState HomeFallbackState,
    SourceReadState State);