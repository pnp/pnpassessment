using PnP.Scanning.Core.Discovery.M1;
using System.Net;
using System.Security.Cryptography;

namespace PnP.Scanning.Core.Discovery;

internal sealed record SharePointFileContent(
    DiscoveryTerminalOutcome Outcome, byte[] Bytes, bool IsComplete,
    AspxHandlerSourceKind Source, AspxHandlerReadState ReadState,
    string EvidenceRef, string ErrorCode = null, int? HttpStatusCode = null,
    bool SameAuthorityConfirmed = false)
{
    internal static SharePointFileContent Unsupported() => new(DiscoveryTerminalOutcome.Unknown,
        Array.Empty<byte>(), false, AspxHandlerSourceKind.Unknown, AspxHandlerReadState.NotRequested,
        "ISharePointAspxRestClient:ReadFileContentAsync:Unsupported", "file_content_unsupported");
}

/// <summary>One authenticated, nonredirecting source request; one deadline covers auth, headers and body.</summary>
internal static class SharePointFileContentReader
{
    internal static Uri CreateSourceUri(Uri web, string path)
    {
        ValidateWeb(web);
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.StartsWith("//") ||
            path.Contains('\\') || path.Contains('?') || path.Contains('#'))
            throw new ArgumentException("A query-independent server-relative source path is required.");
        string decoded = Uri.UnescapeDataString(path);
        if (decoded.Any(char.IsControl) || decoded.Split('/').Any(s => s is "." or "..") ||
            HasEncodedSeparator(path) || HasEncodedSeparator(decoded))
            throw new ArgumentException("Ambiguous source path.");
        string literal = WebUtility.UrlEncode(decoded.Replace("'", "''", StringComparison.Ordinal)).Replace("+", "%20", StringComparison.Ordinal);
        return new Uri(web.AbsoluteUri.TrimEnd('/') + "/_api/web/GetFileByServerRelativePath(decodedUrl=@u)/$value?@u='" + literal + "'");
    }

    internal static void ValidateWeb(Uri web)
    {
        if (web == null || !web.IsAbsoluteUri || web.Scheme != "https" || !web.IsDefaultPort ||
            !string.IsNullOrEmpty(web.UserInfo) || !string.IsNullOrEmpty(web.Query) || !string.IsNullOrEmpty(web.Fragment))
            throw new ArgumentException("An exact HTTPS web authority is required.");
    }

    private static bool HasEncodedSeparator(string value) => value.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("%5c", StringComparison.OrdinalIgnoreCase);

    internal static async Task<SharePointFileContent> ReadAsync(HttpClient client, Uri web, string path,
        int maxBytes, TimeSpan timeout, Func<HttpRequestMessage, CancellationToken, Task> authenticate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(authenticate);
        if (maxBytes < 1 || maxBytes > 16 * 1024 * 1024 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var uri = CreateSourceUri(web, path);
        var evidence = "GET " + AspxDurableRequestEvidence.Endpoint(uri);
        SharePointFileContent Failure(DiscoveryTerminalOutcome state, AspxHandlerReadState read, string code, int? status = null) =>
            new(state, Array.Empty<byte>(), false, AspxHandlerSourceKind.Unknown, read, evidence, code, status);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/octet-stream");
            await authenticate(request, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Failure(DiscoveryTerminalOutcome.Denied, AspxHandlerReadState.Denied, "file_source_denied", status);
            if (status is >= 300 and <= 399 || response.RequestMessage?.RequestUri == null ||
                !string.Equals(response.RequestMessage.RequestUri.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal))
                return Failure(DiscoveryTerminalOutcome.Unknown, AspxHandlerReadState.Unavailable, "file_source_redirect_or_uri_drift", status);
            if (response.StatusCode == HttpStatusCode.PartialContent)
                return Failure(DiscoveryTerminalOutcome.Truncated, AspxHandlerReadState.Truncated, "file_source_partial_content", status);
            if (response.StatusCode != HttpStatusCode.OK)
                return Failure(DiscoveryTerminalOutcome.Failed, AspxHandlerReadState.Unavailable,
                    status == 404 ? "file_source_missing" : "file_source_http_error", status);
            long? length = response.Content.Headers.ContentLength;
            if (length > maxBytes)
                return Failure(DiscoveryTerminalOutcome.Truncated, AspxHandlerReadState.Truncated, "file_source_size_limit", status);
            using var buffer = new MemoryStream();
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            byte[] chunk = new byte[Math.Min(maxBytes + 1, 8192)];
            while (true)
            {
                int count = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maxBytes + 1 - (int)buffer.Length)), deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                buffer.Write(chunk, 0, count);
                if (buffer.Length > maxBytes)
                    return Failure(DiscoveryTerminalOutcome.Truncated, AspxHandlerReadState.Truncated, "file_source_size_limit", status);
            }
            byte[] bytes = buffer.ToArray();
            if (length.HasValue && length.Value != bytes.Length)
                return Failure(DiscoveryTerminalOutcome.Truncated, AspxHandlerReadState.Truncated, "file_source_incomplete_body", status);
            var media = response.Content.Headers.ContentType?.MediaType;
            var semantic = SharePointSemanticDenialDetector.Detect(bytes, media);
            if (semantic.IsDenied)
                return Failure(DiscoveryTerminalOutcome.Denied, AspxHandlerReadState.Denied, "file_source_semantic_denial", status);
            if (semantic.Result == SharePointSemanticDetectorResults.ErrorEnvelope)
                return Failure(DiscoveryTerminalOutcome.Failed, AspxHandlerReadState.Unavailable, "file_source_error_envelope", status);
            if (media == null || media.Contains("html", StringComparison.OrdinalIgnoreCase) || media.Contains("json", StringComparison.OrdinalIgnoreCase))
                return new(DiscoveryTerminalOutcome.Unknown, bytes, true, AspxHandlerSourceKind.RenderedHtml,
                    AspxHandlerReadState.Available, evidence, "file_source_not_source_media", status, true);
            return new(DiscoveryTerminalOutcome.Complete, bytes, true, AspxHandlerSourceKind.ActualFileBytes,
                AspxHandlerReadState.Available, evidence + ";sha256=" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                HttpStatusCode: status, SameAuthorityConfirmed: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return Failure(DiscoveryTerminalOutcome.Truncated, AspxHandlerReadState.Truncated, "file_source_timeout");
        }
        catch (IOException)
        {
            return Failure(DiscoveryTerminalOutcome.Truncated, AspxHandlerReadState.Truncated, "file_source_incomplete_stream");
        }
        catch (HttpRequestException ex)
        {
            return Failure(DiscoveryTerminalOutcome.Failed, AspxHandlerReadState.Unavailable, "file_source_transport_" + ex.GetType().Name);
        }
    }
}
