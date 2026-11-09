#nullable enable
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
using System.Net;

namespace PnP.Scanning.Core.Pipeline.Collection.Audit;

/// <summary>
/// Sends cancellation-aware audit requests with fresh tokens and bounded retries.
/// </summary>
internal static class ClassicPageAuditClient
{
    /// <summary>
    /// Partitions a UTC audit interval into contiguous bounded subwindows.
    /// </summary>
    internal static List<(DateTime Start, DateTime End)> SplitWindow(DateTime start, DateTime end, int chunkDays)
    {
        List<(DateTime, DateTime)> chunks = new List<(DateTime, DateTime)>();
        DateTime cursor = start.ToUniversalTime();
        DateTime endUtc = end.ToUniversalTime();
        while (cursor < endUtc)
        {
            DateTime chunkEnd = cursor.AddDays(chunkDays) < endUtc ? cursor.AddDays(chunkDays) : endUtc;
            chunks.Add((cursor, chunkEnd));
            cursor = chunkEnd;
        }
        return chunks;
    }

    /// <summary>
    /// Sends with a fresh bearer token per attempt and bounded cancellation-aware retry delays.
    /// </summary>
    internal static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client, Func<HttpRequestMessage> requestFactory,
        Func<CancellationToken, Task<string>> tokenProvider, CancellationToken ct)
    {
        int attempts = 0;
        int throttleAttempts = 0;
        while (true)
        {
            using HttpRequestMessage request = requestFactory();
            // Set a FRESH bearer token on every send (including retries). A long-running audit scan
            // can outlive the initial token's ~1h lifetime while Graph queues/processes queries, so a
            // token captured once at the start would be expired by the time we fetch records (HTTP 401).
            // GetAccessTokenAsync is cache-backed, so this is cheap when the token is still valid.
            string token = await tokenProvider(ct);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (++throttleAttempts > 10) return response; // give up after 10 throttle retries
                int wait = 60;
                if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? vals) &&
                    int.TryParse(vals.First(), out int ra)) wait = ra;
                response.Dispose(); // Release the superseded response before retrying.
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                continue;
            }
            if ((int)response.StatusCode is 503 or 504)
            {
                if (++attempts > 3) return response;
                response.Dispose(); // Release the superseded response before retrying.
                await Task.Delay(TimeSpan.FromSeconds(attempts == 1 ? 5 : attempts == 2 ? 15 : 30), ct);
                continue;
            }
            return response;
        }
    }
}
