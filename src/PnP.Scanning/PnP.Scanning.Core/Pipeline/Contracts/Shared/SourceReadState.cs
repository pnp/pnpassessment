#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Shared;

/// <summary>
/// Distinguishes a successful acquisition from denied, failed, omitted, and unknown reads.
/// </summary>
internal sealed record SourceReadState(string Status, string? Error = null)
{
    internal static readonly SourceReadState Complete = new("Complete");
    internal static readonly SourceReadState NotAttempted = new("NotAttempted");
    internal bool Succeeded => Status == "Complete";
}