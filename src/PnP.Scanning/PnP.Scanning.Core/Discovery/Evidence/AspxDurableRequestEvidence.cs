namespace PnP.Scanning.Core.Discovery;

internal static class AspxDurableRequestEvidence
{
    private const string HashedContinuationMarker = "sha256";

    internal static string Endpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrEmpty(endpoint.Query)) return endpoint.AbsoluteUri;
        var query = endpoint.Query[1..].Split('&', StringSplitOptions.None)
            .Select(SanitizeQuerySegment);
        return endpoint.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', query) + endpoint.Fragment;
    }

    internal static bool ContainsRawContinuationValue(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Query))
            return false;
        foreach (var segment in parsed.Query[1..].Split('&', StringSplitOptions.None))
        {
            var separator = segment.IndexOf('=');
            var key = separator < 0 ? segment : segment[..separator];
            if (!IsContinuationKey(key) || separator < 0) continue;
            var value = segment[(separator + 1)..];
            if (!string.IsNullOrEmpty(value) &&
                !string.Equals(value, HashedContinuationMarker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static string Reference(Uri endpoint, SharePointRestPage page, string select)
    {
        if (page == null) return $"GET {Endpoint(endpoint)};$select={select};no-response";
        var received = page.ReceivedAtUtc == default ? DateTimeOffset.UtcNow : page.ReceivedAtUtc;
        return $"GET {Endpoint(page.RequestUri)};$select={select};$filter=;status={(int)page.StatusCode};semantic={page.SemanticDetectorResult};attempt={page.AttemptCount}/{page.AttemptLimit};receivedUtc={received.ToUniversalTime():O};requestId={page.RequestId ?? "unavailable"};correlationId={page.CorrelationId ?? "unavailable"};errorCode={page.ErrorCode ?? "none"};schema={page.SchemaFlavor};digest={page.ResponseDigest}";
    }

    private static string SanitizeQuerySegment(string segment)
    {
        var separator = segment.IndexOf('=');
        var key = separator < 0 ? segment : segment[..separator];
        return IsContinuationKey(key) && separator >= 0 ? key + "=" + HashedContinuationMarker : segment;
    }

    private static bool IsContinuationKey(string encodedKey)
    {
        string key;
        try
        {
            key = Uri.UnescapeDataString(encodedKey.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            key = encodedKey;
        }
        var normalized = key.Trim().TrimStart('$').Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "skiptoken" or "deltatoken" or "continuationtoken" or "nexttoken" or
            "pagetoken" or "pagingtoken" or "paginginfo";
    }
}
