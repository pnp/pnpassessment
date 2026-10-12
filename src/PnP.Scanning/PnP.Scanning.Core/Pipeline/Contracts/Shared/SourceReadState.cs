#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Shared;

/// <summary>
/// Records a source read status and optional diagnostic, keeping unavailable or unattempted values distinct from successful reads.
/// </summary>
internal sealed record SourceReadState(string Status, string? Error = null)
{
    internal static readonly SourceReadState Complete = new("Complete");
    internal static readonly SourceReadState NotAttempted = new("NotAttempted");
    internal bool Succeeded => Status == "Complete";
}
