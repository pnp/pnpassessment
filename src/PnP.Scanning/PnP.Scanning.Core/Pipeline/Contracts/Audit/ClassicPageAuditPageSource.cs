#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Audit;

/// <summary>
/// Preserves one original Graph audit records body, query identity, page ordinal, continuation and HTTP outcome.
/// </summary>
internal sealed record ClassicPageAuditPageSource(int Chunk, string QueryId, int Page, string Body, string? NextLink,
    int StatusCode = 200, string? Error = null);
