#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Audit;

/// <summary>
/// Records a fixed audit subwindow, submitted query identity and settled success, skip or failure status.
/// </summary>
internal sealed record ClassicPageAuditChunkSource(int Chunk, DateTime Start, DateTime End,
    string? QueryId, string Status, string? Error);
