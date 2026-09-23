using System.Net;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal static class SharePointRestResponseParser
{
    internal static SharePointRestPage Parse(Uri requestUri, HttpStatusCode statusCode, byte[] bytes,
        string mediaType = null, string requestId = null, string correlationId = null,
        DateTimeOffset? receivedAtUtc = null, int attemptCount = 1, int attemptLimit = 1)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        bytes ??= Array.Empty<byte>();
        var received = receivedAtUtc ?? DateTimeOffset.UtcNow;
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var semantic = SharePointSemanticDenialDetector.Detect(bytes, mediaType);
        var transportOutcome = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? DiscoveryTerminalOutcome.Denied
            : (int)statusCode >= 200 && (int)statusCode <= 299
                ? DiscoveryTerminalOutcome.Complete : DiscoveryTerminalOutcome.Failed;
        if (transportOutcome != DiscoveryTerminalOutcome.Complete)
            return new(requestUri, statusCode, Array.Empty<JsonElement>(), null, digest, "http-error",
                transportOutcome, semantic.ErrorCode ?? "http_" + (int)statusCode, semantic.Result,
                received, attemptCount, attemptLimit, requestId, correlationId);
        if (semantic.IsDenied)
            return new(requestUri, statusCode, Array.Empty<JsonElement>(), null, digest,
                "semantic-denial-" + semantic.Result, DiscoveryTerminalOutcome.Denied,
                semantic.ErrorCode, semantic.Result, received, attemptCount, attemptLimit, requestId, correlationId);
        if (semantic.Result == SharePointSemanticDetectorResults.ErrorEnvelope)
            return new(requestUri, statusCode, Array.Empty<JsonElement>(), null, digest,
                "semantic-error-envelope", DiscoveryTerminalOutcome.Failed, semantic.ErrorCode,
                semantic.Result, received, attemptCount, attemptLimit, requestId, correlationId);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var parsed = PnPContextSharePointAspxRestClient.ParseEnvelope(document.RootElement);
            return new(requestUri, statusCode, parsed.Items, parsed.NextLink, digest,
                parsed.SchemaFlavor, DiscoveryTerminalOutcome.Complete, null, semantic.Result,
                received, attemptCount, attemptLimit, requestId, correlationId);
        }
        catch (JsonException)
        {
            return new(requestUri, statusCode, Array.Empty<JsonElement>(), null, digest,
                "invalid-json", DiscoveryTerminalOutcome.Failed, "response_json_invalid", semantic.Result,
                received, attemptCount, attemptLimit, requestId, correlationId);
        }
    }
}
