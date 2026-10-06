using PnP.Core;
using System.Net;
using System.Text;

namespace PnP.Scanning.Core.Discovery;

/// <summary>Bounded raw capture followed by strict decoding; never a StreamReader replacement hash.</summary>
internal static class AspxSourceReader
{
    internal const int MaximumCaptureBytes = 16 * 1024 * 1024;
    private const int ContentInspectionCharacters = 131072;

    internal static async Task<AspxSourceReadResult> ReadAsync(AspxFileObservation discovery,
        AspxFileIdentity physicalIdentity, AspxSourceVersion version,
        Func<CancellationToken, Task<Stream>> openStream, CancellationToken token,
        long? expectedByteLength = null, int? httpStatusCode = null, string mediaType = null,
        int maximumCaptureBytes = MaximumCaptureBytes)
    {
        if (maximumCaptureBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCaptureBytes));
        if (expectedByteLength < 0) throw new ArgumentOutOfRangeException(nameof(expectedByteLength));
        token.ThrowIfCancellationRequested();
        var transport = AspxSourceTransportState.Complete;
        var reason = "StreamEndedNormally";
        var capture = AspxSourceCaptureState.Complete;
        byte[] bytes = null;
        using var captured = new MemoryStream();
        try
        {
            using var stream = await openStream(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (stream == null)
                return AspxSourceReadResult.Unavailable(discovery, httpStatusCode is 401 or 403
                    ? AspxSourceTransportState.Denied : httpStatusCode >= 400
                        ? AspxSourceTransportState.Failed : AspxSourceTransportState.NotReturned,
                    httpStatusCode >= 400 ? "TransportHTTP" + httpStatusCode + ";SourceStreamNotReturned" : "SourceStreamNotReturned",
                    physicalIdentity, version, expectedByteLength, httpStatusCode);
            bytes = Array.Empty<byte>(); // The stream was returned, but EOF has not yet been observed.
            var buffer = new byte[8192];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = maximumCaptureBytes - (int)captured.Length;
                if (remaining == 0)
                {
                    transport = AspxSourceTransportState.Partial;
                    capture = AspxSourceCaptureState.Partial;
                    reason = "CaptureLimitReached;EndOfStreamNotObserved";
                    break;
                }
                var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (count == 0) break;
                captured.Write(buffer, 0, count);
            }
            bytes = captured.ToArray();
            if (expectedByteLength.HasValue && expectedByteLength != bytes.LongLength)
            {
                transport = AspxSourceTransportState.Partial;
                if (expectedByteLength > bytes.LongLength) capture = AspxSourceCaptureState.Partial;
                reason = "ExpectedFileLengthMismatch";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            token.ThrowIfCancellationRequested();
            httpStatusCode ??= HttpStatus(ex);
            transport = IsDenied(ex) ? AspxSourceTransportState.Denied : AspxSourceTransportState.Failed;
            reason = AssessmentWebDiscovery.ErrorCode(ex) + ":" + ex.Message;
            if (bytes != null)
            {
                bytes = captured.ToArray();
                capture = AspxSourceCaptureState.Partial;
                if (transport != AspxSourceTransportState.Denied) transport = AspxSourceTransportState.Partial;
                reason = "StreamReadFailed:" + reason;
            }
            else capture = AspxSourceCaptureState.NotCaptured;
        }
        token.ThrowIfCancellationRequested();
        // Response status and capture completeness are independent; preserve denial/error bodies.
        if (httpStatusCode is 401 or 403)
        {
            transport = AspxSourceTransportState.Denied;
            reason = "TransportHTTP" + httpStatusCode + ";" + reason;
        }
        else if (httpStatusCode >= 400)
        {
            transport = AspxSourceTransportState.Failed;
            reason = "TransportHTTP" + httpStatusCode + ";" + reason;
        }
        var (decoding, text) = Decode(bytes);
        var (content, contentReason) = bytes?.Length == 0 && capture != AspxSourceCaptureState.Complete
            ? (AspxSourceContentState.NotInspected, "NoCapturedBytes;EndOfStreamNotObserved")
            : InspectContent(bytes, text, decoding, mediaType);
        return new(discovery, physicalIdentity, version, transport, reason, capture, bytes,
            expectedByteLength, decoding, text, content, contentReason, httpStatusCode);
    }

    internal static int? HttpStatus(Exception error) => error switch
    {
        HttpRequestException { StatusCode: not null } http => (int)http.StatusCode.Value,
        ServiceException { Error: ServiceError service } => service.HttpResponseCode,
        _ => null,
    };

    internal static bool IsDenied(Exception error) => HttpStatus(error) is 401 or 403 ||
        error is UnauthorizedAccessException;

    private static (AspxSourceDecoding Decoding, string Text) Decode(byte[] bytes)
    {
        if (bytes == null) return (new(AspxSourceDecodingState.NotAttempted, null, 0, "NoBytesCaptured"), null);
        Encoding encoding = new UTF8Encoding(false, true);
        var name = "utf-8";
        var bom = 0;
        var selection = "StrictUtf8AssumptionWithoutBom";
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }))
        {
            encoding = new UTF32Encoding(false, true, true); name = "utf-32le"; bom = 4;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
        {
            encoding = new UTF32Encoding(true, true, true); name = "utf-32be"; bom = 4;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bom = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        {
            encoding = new UnicodeEncoding(false, true, true); name = "utf-16le"; bom = 2;
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            encoding = new UnicodeEncoding(true, true, true); name = "utf-16be"; bom = 2;
        }
        if (bom > 0) selection = "BomSelectedStrictDecoder";
        try
        {
            var text = encoding.GetString(bytes, bom, bytes.Length - bom);
            // BOM-less UTF-16 often happens to be valid UTF-8 containing NULs. Do not guess a code page.
            if (text.Contains('\0')) return (new(AspxSourceDecodingState.Unreliable, name, bom,
                "UnexpectedNul;EncodingNotSafelyEstablished"), null);
            return (new(AspxSourceDecodingState.Reliable, name, bom, selection), text);
        }
        catch (DecoderFallbackException)
        {
            return (new(AspxSourceDecodingState.Unreliable, name, bom, "StrictDecoderRejectedBytes"), null);
        }
    }

    private static (AspxSourceContentState State, string Reason) InspectContent(byte[] bytes,
        string text, AspxSourceDecoding decoding, string mediaType)
    {
        if (bytes == null) return (AspxSourceContentState.NotInspected, "NoBytesCaptured");
        if (bytes.Length == 0) return (AspxSourceContentState.Empty, "ObservedZeroByteResponse");
        if (decoding.State != AspxSourceDecodingState.Reliable)
            return (AspxSourceContentState.Unknown, "SourceDecodingUnreliable");
        if (string.IsNullOrWhiteSpace(text)) return (AspxSourceContentState.Empty, "ObservedWhitespaceOnlySource");
        var trimmed = text.AsSpan(0, Math.Min(text.Length, ContentInspectionCharacters)).TrimStart();
        // Recognize only a leading server-source preamble, not directive-looking strings in HTML.
        // Page attribute parsing and declared/default projection belong to the parser consumer.
        while (trimmed.StartsWith("<%--", StringComparison.Ordinal) || trimmed.StartsWith("<!--", StringComparison.Ordinal))
        {
            var server = trimmed.StartsWith("<%--", StringComparison.Ordinal);
            var end = trimmed.IndexOf((server ? "--%>" : "-->").AsSpan(), StringComparison.Ordinal);
            if (end < 0) break;
            trimmed = trimmed[(end + (server ? 4 : 3))..].TrimStart();
        }
        if (trimmed.StartsWith("<%@", StringComparison.Ordinal) ||
            trimmed.StartsWith("<%", StringComparison.Ordinal) && !trimmed.StartsWith("<%--", StringComparison.Ordinal))
            return (AspxSourceContentState.Source, "PhysicalDownloadWithServerSourcePreamble");
        // Reuse the existing denial detector after strict decoding, including UTF-16 responses.
        // This temporary UTF-8 detector input is never substituted for raw-byte digest evidence.
        var sample = text[..Math.Min(text.Length, ContentInspectionCharacters)];
        var semantic = SharePointSemanticDenialDetector.Detect(Encoding.UTF8.GetBytes(sample), mediaType);
        if (semantic.Result == SharePointSemanticDetectorResults.LoginShell)
            return (AspxSourceContentState.LoginShell, semantic.ErrorCode);
        if (semantic.IsDenied) return (AspxSourceContentState.SemanticDenied, semantic.ErrorCode);
        if (trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            return (AspxSourceContentState.RenderedHtml, "RenderedHtmlWithoutServerSourcePreamble");
        return (AspxSourceContentState.Unknown, "PhysicalSourceNotReliablyRecognized");
    }
}
