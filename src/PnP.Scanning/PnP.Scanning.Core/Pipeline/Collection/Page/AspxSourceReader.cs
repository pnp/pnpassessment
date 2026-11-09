using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using PnP.Core.Model.SharePoint;
using PnP.Scanning.Core.Pipeline.Analysis.Page;

namespace PnP.Scanning.Core.Pipeline.Collection.Page;

internal sealed record AspxSourceObservation(string State, byte[]? Bytes, string? Error);

internal static class AspxSourceReader
{
    internal static async Task<AspxSourceObservation> ReadAsync(IFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        if (file.Length > AspxSourceParser.MaximumSourceBytes)
            return new("TooLarge", null, "SourceExceedsAnalysisLimit");
        using var stream = await file.GetContentAsync(streamContent: true).ConfigureAwait(false);
        return await ReadStreamAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<AspxSourceObservation> ReadStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var bytes = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > AspxSourceParser.MaximumSourceBytes)
                return new("TooLarge", null, "SourceExceedsAnalysisLimit");
            await bytes.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new("Available", bytes.ToArray(), null);
    }
}
