#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Audit;

/// <summary>
/// Retains an original audit response body, HTTP outcome, and pagination receipt.
/// </summary>
internal sealed record ClassicPageAuditPageSource(
    int Chunk,
    string QueryId,
    int Page,
    string Body,
    string? NextLink,
    int StatusCode = 200,
    string? Error = null);