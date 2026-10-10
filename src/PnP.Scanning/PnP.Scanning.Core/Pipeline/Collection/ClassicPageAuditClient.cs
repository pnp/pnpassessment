using System.Net;

namespace PnP.Scanning.Core.Pipeline.Collection;

internal static class ClassicPageAuditClient
{
    internal static List<(DateTime Start, DateTime End)> SplitWindow(DateTime start, DateTime end, int chunkDays)
    {
        var chunks = new List<(DateTime, DateTime)>();
        var cursor = start.ToUniversalTime();
        var endUtc = end.ToUniversalTime();
        while (cursor < endUtc)
        {
            var chunkEnd = cursor.AddDays(chunkDays) < endUtc ? cursor.AddDays(chunkDays) : endUtc;
            chunks.Add((cursor, chunkEnd));
            cursor = chunkEnd;
        }
        return chunks;
    }

    internal static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client, Func<HttpRequestMessage> requestFactory,
        Func<CancellationToken, Task<string>> tokenProvider, CancellationToken ct)
    {
        int attempts = 0;
        int throttleAttempts = 0;
        while (true)
        {
            var request = requestFactory();
            // Set a FRESH bearer token on every send (including retries). A long-running audit scan
            // can outlive the initial token's ~1h lifetime while Graph queues/processes queries, so a
            // token captured once at the start would be expired by the time we fetch records (HTTP 401).
            // GetAccessTokenAsync is cache-backed, so this is cheap when the token is still valid.
            var token = await tokenProvider(ct);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (++throttleAttempts > 10) return response; // give up after 10 throttle retries
                int wait = 60;
                if (response.Headers.TryGetValues("Retry-After", out var vals) &&
                    int.TryParse(vals.First(), out int ra)) wait = ra;
                response.Dispose(); // superseded — release before retrying so retries don't leak responses
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                continue;
            }
            if ((int)response.StatusCode is 503 or 504)
            {
                if (++attempts > 3) return response;
                response.Dispose(); // superseded — release before retrying so retries don't leak responses
                await Task.Delay(TimeSpan.FromSeconds(attempts == 1 ? 5 : attempts == 2 ? 15 : 30), ct);
                continue;
            }
            return response;
        }
    }
}
