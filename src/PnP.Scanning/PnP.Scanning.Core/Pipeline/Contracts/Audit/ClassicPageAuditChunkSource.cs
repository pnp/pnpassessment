#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Audit;

internal sealed record ClassicPageAuditChunkSource(int Chunk, DateTime Start, DateTime End,
    string? QueryId, string Status, string? Error);
