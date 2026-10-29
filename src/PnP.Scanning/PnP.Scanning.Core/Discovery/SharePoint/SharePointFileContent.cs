using PnP.Scanning.Core.Discovery.Aspx;
using System.Net;
using System.Security.Cryptography;

namespace PnP.Scanning.Core.Discovery;

internal sealed record SharePointFileContent(
    DiscoveryTerminalOutcome Outcome, byte[] Bytes, bool IsComplete,
    AspxHandlerSourceKind Source, AspxHandlerReadState ReadState,
    string EvidenceRef, string ErrorCode = null, int? HttpStatusCode = null,
    bool SameAuthorityConfirmed = false)
{
    /// <summary>Returns an explicit unavailable capability result rather than treating an unrequested file read as empty source.</summary>
    internal static SharePointFileContent Unsupported() => new(DiscoveryTerminalOutcome.Unknown,
        Array.Empty<byte>(), false, AspxHandlerSourceKind.Unknown, AspxHandlerReadState.NotRequested,
        "ISharePointAspxRestClient:ReadFileContentAsync:Unsupported", "file_content_unsupported");
}

/// <summary>One authenticated, nonredirecting source request; one deadline covers auth, headers and body.</summary>
internal static class SharePointFileContentReader
{
    private const string SourceMediaType = "application/octet-stream";
    private const string FileSourceEndpoint = "/_api/web/GetFileByServerRelativePath(decodedUrl=@u)/$value?@u='";

    private static class SourceReadErrors
    {
        internal const string Denied = "file_source_denied";
        internal const string RedirectOrUriDrift = "file_source_redirect_or_uri_drift";
        internal const string PartialContent = "file_source_partial_content";
        internal const string Missing = "file_source_missing";
        internal const string HttpError = "file_source_http_error";
        internal const string SizeLimit = "file_source_size_limit";
        internal const string IncompleteBody = "file_source_incomplete_body";
        internal const string SemanticDenial = "file_source_semantic_denial";
        internal const string ErrorEnvelope = "file_source_error_envelope";
        internal const string NotSourceMedia = "file_source_not_source_media";
        internal const string Timeout = "file_source_timeout";
        internal const string IncompleteStream = "file_source_incomplete_stream";
        internal const string TransportPrefix = "file_source_transport_";
    }

    /// <summary>Builds the exact file-source endpoint after rejecting ambiguous or query-bearing server-relative paths.</summary>
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
        return new Uri(web.AbsoluteUri.TrimEnd('/') + FileSourceEndpoint + literal + "'");
    }

    /// <summary>Requires an exact default-port HTTPS authority with no credentials, query, or fragment before authentication.</summary>
    internal static void ValidateWeb(Uri web)
    {
        if (web == null || !web.IsAbsoluteUri || web.Scheme != "https" || !web.IsDefaultPort ||
            !string.IsNullOrEmpty(web.UserInfo) || !string.IsNullOrEmpty(web.Query) || !string.IsNullOrEmpty(web.Fragment))
            throw new ArgumentException("An exact HTTPS web authority is required.");
    }

    /// <summary>Detects percent-encoded path separators without normalizing away ambiguous input.</summary>
    private static bool HasEncodedSeparator(string value) => value.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("%5c", StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies one deadline and byte budget across authentication and transfer; only complete same-URI source responses become actual-file evidence.</summary>
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
            request.Headers.Accept.ParseAdd(SourceMediaType);
            await authenticate(request, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Failure(DiscoveryTerminalOutcome.Denied, AspxHandlerReadState.Denied, SourceReadErrors.Denied, status);
            if (status is >= 300 and <= 399 || response.RequestMessage?.RequestUri == null ||
                !string.Equals(response.RequestMessage.RequestUri.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal))
                return Failure(DiscoveryTerminalOutcome.Unknown, AspxHandlerReadState.Unavailable, SourceReadErrors.RedirectOrUriDrift, status);
            if (response.StatusCode == HttpStatusCode.PartialContent)
                return Failure(DiscoveryTerminalOutcome.Truncated, AspxHandlerReadState.Truncated, SourceReadErrors.PartialContent, status);
            if (response.StatusCode != HttpStatusCode.OK)
                return Failure(DiscoveryTerminalOutcome.Failed, AspxHandlerReadState.Unavailable,
                    status == 404 ? SourceReadErrors.Missing : SourceReadErrors.HttpError, status);
            long? length = response.Content.Headers.ContentLength;
            if (length > maxBytes)
                return Failure(DiscoveryTerminalOutcome.Truncated, AspxHandlerReadState.Truncated, SourceReadErrors.SizeLimit, status);
            using var buffer = new MemoryStream();
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            byte[] chunk = new byte[Math.Min(maxBytes + 1, 8192)];
            while (true)
            {
                int count = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maxBytes + 1 - (int)buffer.Length)), deadline.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                buffer.Write(chunk, 0, count);
                if (buffer.Length > maxBytes)
                    return Failure(DiscoveryTerminalOutcome.Truncated, AspxHandlerReadState.Truncated, SourceReadErrors.SizeLimit, status);
            }
            byte[] bytes = buffer.ToArray();
            if (length.HasValue && length.Value != bytes.Length)
                return Failure(DiscoveryTerminalOutcome.Truncated, AspxHandlerReadState.Truncated, SourceReadErrors.IncompleteBody, status);
            var media = response.Content.Headers.ContentType?.MediaType;
            var semantic = SharePointSemanticDenialDetector.Detect(bytes, media);
            if (semantic.IsDenied)
                return Failure(DiscoveryTerminalOutcome.Denied, AspxHandlerReadState.Denied, SourceReadErrors.SemanticDenial, status);
            if (semantic.Result == SharePointSemanticDetectorResults.ErrorEnvelope)
                return Failure(DiscoveryTerminalOutcome.Failed, AspxHandlerReadState.Unavailable, SourceReadErrors.ErrorEnvelope, status);
            if (media == null || media.Contains("html", StringComparison.OrdinalIgnoreCase) || media.Contains("json", StringComparison.OrdinalIgnoreCase))
                return new(DiscoveryTerminalOutcome.Unknown, bytes, true, AspxHandlerSourceKind.RenderedHtml,
                    AspxHandlerReadState.Available, evidence, SourceReadErrors.NotSourceMedia, status, true);
            return new(DiscoveryTerminalOutcome.Complete, bytes, true, AspxHandlerSourceKind.ActualFileBytes,
                AspxHandlerReadState.Available, evidence + ";sha256=" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                HttpStatusCode: status, SameAuthorityConfirmed: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(DiscoveryTerminalOutcome.Truncated, AspxHandlerReadState.Truncated, SourceReadErrors.Timeout);
        }
        catch (IOException)
        {
            return Failure(DiscoveryTerminalOutcome.Truncated, AspxHandlerReadState.Truncated, SourceReadErrors.IncompleteStream);
        }
        catch (HttpRequestException ex)
        {
            return Failure(DiscoveryTerminalOutcome.Failed, AspxHandlerReadState.Unavailable, SourceReadErrors.TransportPrefix + ex.GetType().Name);
        }
    }
}
