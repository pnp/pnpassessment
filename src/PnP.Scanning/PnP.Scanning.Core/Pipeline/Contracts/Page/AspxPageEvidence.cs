#nullable enable
namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

/// <summary>
/// Retains original source bytes and independently acquired handler and role evidence states.
/// </summary>
internal sealed record AspxPageEvidence(
    string SourceReadState,
    string? SourceSha256,
    string? PageHandler,
    string HandlerResolution,
    string? PageDeclaration,
    string? SourceError,
    int? ListTemplate,
    bool? IsCatalog,
    bool? IsListView,
    bool? IsListForm,
    string RoleReadState,
    DateTime ObservedAtUtc,
    byte[]? OriginalBytes = null);