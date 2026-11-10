#nullable enable

namespace PnP.Scanning.Core.ClassicPages.Contracts.Audit;

/// <summary>
/// Retains query identity and settled acquisition coverage for one fixed audit subwindow.
/// </summary>
internal sealed record ClassicPageAuditChunkSource(
    int Chunk,
    DateTime Start,
    DateTime End,
    string? QueryId,
    string Status,
    string? Error);