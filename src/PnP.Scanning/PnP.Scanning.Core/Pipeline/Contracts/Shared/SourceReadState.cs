#nullable enable

namespace PnP.Scanning.Core.Pipeline.Contracts.Shared;

internal sealed record SourceReadState(string Status, string? Error = null)
{
    internal static readonly SourceReadState Complete = new("Complete");
    internal static readonly SourceReadState NotAttempted = new("NotAttempted");
    internal bool Succeeded => Status == "Complete";
}
