#nullable enable
namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

internal sealed record AspxPageEvidence(
    string SourceReadState, string? SourceSha256, string? PageHandler, string HandlerResolution,
    string? PageDeclaration, string? SourceError, int? ListTemplate, bool? IsCatalog,
    bool? IsListView, bool? IsListForm, string RoleReadState, DateTime ObservedAtUtc, byte[]? OriginalBytes = null);
