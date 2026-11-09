using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using PnP.Core.Model.SharePoint;
using PnP.Scanning.Core.Pipeline.Analysis.Page;

namespace PnP.Scanning.Core.Pipeline.Collection.Page;

/// <summary>
/// Preserves acquired source bytes or the explicit reason content was unavailable.
/// </summary>
internal sealed record AspxSourceObservation(string State, byte[]? Bytes, string? Error);

/// <summary>
/// Reads original ASPX bytes under a fixed size bound while observing cancellation.
/// </summary>
internal static class AspxSourceReader
{
    /// <summary>
    /// Reads original file bytes only when the observed size fits the source limit.
    /// </summary>
    internal static async Task<AspxSourceObservation> ReadAsync(IFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        if (file.Length > AspxSourceParser.MaximumSourceBytes)
            return new("TooLarge", null, "SourceExceedsAnalysisLimit");
        using Stream? stream = await file.GetContentAsync(streamContent: true).ConfigureAwait(false);
        return await ReadStreamAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies original stream bytes under the source bound and propagates cancellation.
    /// </summary>
    internal static async Task<AspxSourceObservation> ReadStreamAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream? bytes = new MemoryStream();
        byte[]? buffer = new byte[16 * 1024];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > AspxSourceParser.MaximumSourceBytes)
                return new("TooLarge", null, "SourceExceedsAnalysisLimit");
            await bytes.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new("Available", bytes.ToArray(), null);
    }
}