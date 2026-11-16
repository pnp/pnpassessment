#nullable enable
using PnP.Scanning.Core.ClassicPages.Analysis.Page;

namespace PnP.Scanning.Core.ClassicPages.Collection.Page;

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
    /// Reads original file bytes in one request addressed by the file's unique id. The size the inventory observed
    /// decides whether the request is sent, and the response is bounded again while it is read, so a file larger
    /// than the source limit is reported as too large in both cases.
    /// </summary>
    internal static async Task<AspxSourceObservation> ReadAsync(
        HttpClient client,
        Func<HttpRequestMessage, Task> authenticate,
        Uri web,
        Guid fileUniqueId,
        long? observedLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(authenticate);
        ArgumentNullException.ThrowIfNull(web);
        cancellationToken.ThrowIfCancellationRequested();
        if (observedLength > AspxSourceParser.MaximumSourceBytes)
            return new("TooLarge", null, "SourceExceedsAnalysisLimit");
        using HttpRequestMessage request = new(HttpMethod.Get, SourceUri(web, fileUniqueId));
        await authenticate(request).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException(
                $"The ASPX source request was denied with HTTP {(int)response.StatusCode}.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"The ASPX source request failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        if (response.Content.Headers.ContentLength > AspxSourceParser.MaximumSourceBytes)
            return new("TooLarge", null, "SourceExceedsAnalysisLimit");
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStreamAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Addresses a file's original bytes by its unique id within the Web that owns it.
    /// </summary>
    internal static Uri SourceUri(Uri web, Guid fileUniqueId) =>
        new(web.AbsoluteUri.TrimEnd('/') + $"/_api/web/GetFileById('{fileUniqueId:D}')/$value");

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