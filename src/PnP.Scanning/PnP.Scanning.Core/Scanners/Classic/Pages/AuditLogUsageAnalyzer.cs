using PnP.Scanning.Core.Storage;
using System.Net;
using System.Text.Json;
using System.Collections.Generic;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Queries the Microsoft Graph <c>security/auditLog/queries</c> (beta) API for classic
    /// SharePoint page audit events (ClassicPageViewed, ClassicPageCreated, ClassicPageEdited)
    /// and returns a per-page stats dictionary keyed by absolute page URL.
    /// The caller (StorageManager) filters the returned dictionary per scanned site collection
    /// using a URL prefix match.
    ///
    /// Retention: 180 days (Audit Standard) / 1 year (Audit Premium / E5).
    /// Required permission: AuditLogsQuery-SharePoint.Read.All (application).
    ///
    /// Graph audit log query flow — chunked parallel design:
    ///   1. Split the requested time window into ChunkDays-sized sub-windows
    ///   2. Submit all sub-window queries in parallel (POST /beta/security/auditLog/queries)
    ///   3. Poll all queries in parallel every PollInterval until all reach "succeeded"
    ///   4. Fetch records from each query ($top=5000, follow @odata.nextLink)
    ///   5. Merge results — sum counts per pageUrl across chunks
    ///
    /// Chunking avoids the server-side timeout that occurs when a single large query
    /// (e.g., 14 days × 100k pages) takes longer than QueryTimeout to process.
    ///
    /// Note: UniqueUsers across chunk boundaries may be slightly over-counted if the
    /// same user visited a page in multiple sub-windows. This is an acceptable approximation.
    ///
    /// Record field mapping (beta): "operation", "objectId", "userId" are all top-level properties.
    /// The "auditData" field is a nested object and is NOT used.
    /// </summary>
    internal static class AuditLogUsageAnalyzer
    {
        internal readonly record struct AuditPageStats(int ViewsCount, int CreatesCount, int EditsCount, int UniqueUsers);

        private static readonly HashSet<string> ViewOperations = new(StringComparer.OrdinalIgnoreCase)
            { "ClassicPageViewed" };
        private static readonly HashSet<string> CreateOperations = new(StringComparer.OrdinalIgnoreCase)
            { "ClassicPageCreated" };
        private static readonly HashSet<string> EditOperations = new(StringComparer.OrdinalIgnoreCase)
            { "ClassicPageEdited" };

        private static readonly TimeSpan PollInterval   = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan QueryTimeout   = TimeSpan.FromMinutes(45);
        private const int ChunkDays         = 2;    // each sub-query covers 2 days — keeps server-side processing fast
        private const int PageSize          = 5000;  // $top per records page — 5× fewer HTTP round trips than 1000
        private const int MaxParallelChunks = 7;     // cap concurrent Graph queries — avoids flooding the API for large windows (e.g. 180d = 90 chunks)
        // Memory safety: store the hash of userId (int, 4 bytes) instead of the full string (~50 bytes).
        // Accepts a tiny false-positive rate on UniqueUsers count (hash collision) in exchange for ~12× less memory.
        // Also caps the set at MaxTrackedUsersPerPage — beyond this the page is clearly "heavily used" and the
        // exact count matters less; memory is bounded to MaxTrackedUsersPerPage × 4 bytes per page.
        private const int MaxTrackedUsersPerPage = 10_000;

        /// <summary>
        /// Pure: applies audit stats to the record. If stats is null or the pageUrl key is not found,
        /// leaves counts at 0.
        /// </summary>
        internal static void ApplyAuditUsage(ClassicPageAuditUsage record, IReadOnlyDictionary<string, AuditPageStats> stats)
        {
            if (stats == null || !stats.TryGetValue(record.PageUrl, out var pageStats))
                return;

            record.AuditViewsCount = pageStats.ViewsCount;
            record.AuditCreatesCount = pageStats.CreatesCount;
            record.AuditEditsCount = pageStats.EditsCount;
            record.AuditUniqueUsers = pageStats.UniqueUsers;
        }

        /// <summary>
        /// Integration-only: splits the window into ChunkDays sub-windows, submits up to
        /// MaxParallelChunks queries concurrently, merges results, and returns a per-page
        /// stats dictionary. Reports progress via <paramref name="progress"/> when supplied.
        /// Returns (null, skipReason) on permission error, query failure, or timeout.
        /// </summary>
        internal static async Task<(IReadOnlyDictionary<string, AuditPageStats> Stats, string SkipReason)> QueryAllSitesAuditUsageAsync(
            HttpClient httpClient, string graphBaseUrl, string bearerToken,
            IReadOnlyList<string> siteUrls,
            DateTime windowStart, DateTime windowEnd,
            CancellationToken cancellationToken,
            Action<string> progress = null)
        {
            var chunks = SplitWindow(windowStart, windowEnd, ChunkDays);
            int total = chunks.Count;

            progress?.Invoke($"Submitting {total} audit log quer{(total == 1 ? "y" : "ies")} " +
                             $"({ChunkDays}-day chunks, up to {MaxParallelChunks} parallel) " +
                             $"for window {windowStart:yyyy-MM-dd} → {windowEnd:yyyy-MM-dd}");

            // Cap concurrency so we don't flood Graph with 90 simultaneous queries for large windows
            using var semaphore = new SemaphoreSlim(MaxParallelChunks);
            int completed = 0;

            var tasks = chunks.Select(async c =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var result = await QueryChunkAsync(
                        httpClient, graphBaseUrl, bearerToken, siteUrls,
                        c.Start, c.End, cancellationToken);

                    int done = System.Threading.Interlocked.Increment(ref completed);
                    progress?.Invoke($"Audit log query {done}/{total} completed ({c.Start:MM-dd} → {c.End:MM-dd})");
                    return result;
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            var chunkResults = await Task.WhenAll(tasks);

            // Partial-success: use data from succeeded chunks; warn about failed ones.
            // Dropping everything because one chunk timed out would throw away valid data from other chunks.
            var succeeded = chunkResults.Where(r => r.Stats != null).Select(r => r.Stats).ToList();
            var failures  = chunkResults.Where(r => r.Stats == null).Select(r => r.SkipReason).ToList();

            if (succeeded.Count == 0)
                return (null, failures.FirstOrDefault() ?? "No chunks to query"); // all chunks failed (or window was empty)

            var merged = MergeChunks(succeeded);

            if (failures.Count > 0)
            {
                // Partial success: return the merged stats AND a non-null skipReason so the caller
                // can mark rows as "partial" rather than "succeeded", making the gap visible in the CSV.
                string partialReason = $"PartialData: {failures.Count}/{total} chunk(s) failed — {string.Join("; ", failures.Distinct())}";
                progress?.Invoke($"WARNING: {partialReason}");
                progress?.Invoke($"Audit log collection partial: {merged.Count} pages with events across {succeeded.Count}/{total} chunks");
                return (merged, partialReason);
            }

            progress?.Invoke($"Audit log collection done: {merged.Count} pages with events across {succeeded.Count}/{total} chunks");
            return (merged, null);
        }

        // ── private helpers ──────────────────────────────────────────────────────

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

        internal static IReadOnlyDictionary<string, AuditPageStats> MergeChunks(
            IEnumerable<IReadOnlyDictionary<string, AuditPageStats>> chunks)
        {
            var merged = new Dictionary<string, (int Views, int Creates, int Edits, int UniqueUsers)>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in chunks)
            {
                foreach (var kvp in chunk)
                {
                    if (!merged.TryGetValue(kvp.Key, out var existing))
                        merged[kvp.Key] = (kvp.Value.ViewsCount, kvp.Value.CreatesCount, kvp.Value.EditsCount, kvp.Value.UniqueUsers);
                    else
                        merged[kvp.Key] = (
                            existing.Views   + kvp.Value.ViewsCount,
                            existing.Creates + kvp.Value.CreatesCount,
                            existing.Edits   + kvp.Value.EditsCount,
                            existing.UniqueUsers + kvp.Value.UniqueUsers); // slight over-count if same user across chunks — acceptable approximation
                }
            }
            return merged.ToDictionary(
                kvp => kvp.Key,
                kvp => new AuditPageStats(kvp.Value.Views, kvp.Value.Creates, kvp.Value.Edits, kvp.Value.UniqueUsers),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Submits, polls, and fetches one sub-window Graph query.</summary>
        private static async Task<(IReadOnlyDictionary<string, AuditPageStats> Stats, string SkipReason)> QueryChunkAsync(
            HttpClient httpClient, string graphBaseUrl, string bearerToken,
            IReadOnlyList<string> siteUrls,
            DateTime chunkStart, DateTime chunkEnd,
            CancellationToken cancellationToken)
        {
            string queriesUrl = $"https://{graphBaseUrl}/beta/security/auditLog/queries";

            // Step 1: Submit
            string queryId;
            try
            {
                var queryBody = new Dictionary<string, object>
                {
                    ["filterStartDateTime"] = chunkStart.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["filterEndDateTime"]   = chunkEnd.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ["serviceFilter"]       = "SharePoint",
                    ["recordTypeFilters"]   = new[] { "SharePoint" },
                    ["operationFilters"]    = new[] { "ClassicPageViewed", "ClassicPageCreated", "ClassicPageEdited" },
                };
                if (siteUrls != null && siteUrls.Count > 0)
                    queryBody["objectIdFilters"] = siteUrls.Select(u => u.TrimEnd('/') + "/*").ToArray();

                var body = JsonSerializer.Serialize(queryBody);

                var postResponse = await SendWithRetryAsync(httpClient, () =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, queriesUrl);
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
                    req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                    return req;
                }, cancellationToken);

                if (postResponse.StatusCode == HttpStatusCode.Forbidden)
                    return (null, "NoPermission: The Entra app is missing the 'AuditLogsQuery-SharePoint.Read.All' application permission for Microsoft Graph. Add it in Entra Portal → API permissions and grant admin consent.");

                var postBody = await postResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!postResponse.IsSuccessStatusCode)
                    return (null, $"SubmitError: HTTP {(int)postResponse.StatusCode}: {postBody[..Math.Min(200, postBody.Length)]}");

                using var postDoc = JsonDocument.Parse(postBody);
                queryId = postDoc.RootElement.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(queryId))
                    return (null, "SubmitError: response contained no query id");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return (null, $"Error: {ex.Message}"); }

            // Step 2: Poll
            var deadline = DateTime.UtcNow.Add(QueryTimeout);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(PollInterval, cancellationToken);

                if (DateTime.UtcNow >= deadline)
                    return (null, $"QueryTimeout: query {queryId} did not complete within {QueryTimeout.TotalMinutes} minutes");

                HttpResponseMessage pollResponse;
                try
                {
                    pollResponse = await SendWithRetryAsync(httpClient, () =>
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, $"{queriesUrl}/{queryId}");
                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
                        return req;
                    }, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return (null, $"PollError: {ex.Message}"); }

                if (!pollResponse.IsSuccessStatusCode)
                {
                    var errBody = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
                    return (null, $"PollError: HTTP {(int)pollResponse.StatusCode}: {errBody[..Math.Min(200, errBody.Length)]}");
                }

                var pollBody = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
                string status;
                try
                {
                    using var pollDoc = JsonDocument.Parse(pollBody);
                    status = pollDoc.RootElement.GetProperty("status").GetString() ?? string.Empty;
                }
                catch (Exception ex) { return (null, $"ParseError polling query {queryId}: {ex.Message}"); }

                if (status == "failed")
                    return (null, $"QueryFailed: query {queryId} reported failed status");
                if (status == "succeeded")
                    break;
            }

            // Step 3: Fetch records ($top=5000 to minimise round trips)
            // HashSet<int> stores hash(userId) — 4 bytes/entry vs ~50 bytes for full string; bounded by MaxTrackedUsersPerPage
            var results = new Dictionary<string, (int Views, int Creates, int Edits, HashSet<int> Users)>(StringComparer.OrdinalIgnoreCase);
            string nextLink = $"{queriesUrl}/{queryId}/records?$top={PageSize}";

            while (nextLink != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                HttpResponseMessage recordsResponse;
                try
                {
                    recordsResponse = await SendWithRetryAsync(httpClient, () =>
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, nextLink);
                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
                        return req;
                    }, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return (null, $"RecordsError: {ex.Message}"); }

                if (!recordsResponse.IsSuccessStatusCode)
                {
                    var errBody = await recordsResponse.Content.ReadAsStringAsync(cancellationToken);
                    return (null, $"RecordsError: HTTP {(int)recordsResponse.StatusCode}: {errBody[..Math.Min(200, errBody.Length)]}");
                }

                var recordsBody = await recordsResponse.Content.ReadAsStringAsync(cancellationToken);
                JsonDocument recordsDoc;
                try { recordsDoc = JsonDocument.Parse(recordsBody); }
                catch (JsonException ex) { return (null, $"ParseError fetching records for query {queryId}: {ex.Message}"); }

                using (recordsDoc)
                {
                if (!recordsDoc.RootElement.TryGetProperty("value", out var valueElement))
                    return (null, $"ParseError: records response for query {queryId} missing 'value' array");

                foreach (var record in valueElement.EnumerateArray())
                {
                    if (!record.TryGetProperty("operation", out var opProp)) continue;
                    string operation = opProp.GetString() ?? string.Empty;

                    if (!record.TryGetProperty("objectId", out var objProp)) continue;
                    string pageUrl = objProp.GetString();
                    if (string.IsNullOrEmpty(pageUrl)) continue;
                    if (!pageUrl.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)) continue;

                    string userId = record.TryGetProperty("userId", out var uidProp)
                        ? uidProp.GetString() ?? string.Empty : string.Empty;

                    if (!results.TryGetValue(pageUrl, out var existing))
                    {
                        existing = (0, 0, 0, new HashSet<int>());
                        results[pageUrl] = existing;
                    }
                    int views   = existing.Views   + (ViewOperations.Contains(operation)   ? 1 : 0);
                    int creates = existing.Creates + (CreateOperations.Contains(operation) ? 1 : 0);
                    int edits   = existing.Edits   + (EditOperations.Contains(operation)   ? 1 : 0);
                    results[pageUrl] = (views, creates, edits, existing.Users);
                    if (!string.IsNullOrEmpty(userId) && existing.Users.Count < MaxTrackedUsersPerPage)
                        existing.Users.Add(StringComparer.OrdinalIgnoreCase.GetHashCode(userId));
                }

                nextLink = recordsDoc.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkProp)
                    ? nextLinkProp.GetString() : null;
                } // end using (recordsDoc)
            }

            var output = new Dictionary<string, AuditPageStats>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in results)
                output[kvp.Key] = new AuditPageStats(kvp.Value.Views, kvp.Value.Creates, kvp.Value.Edits, kvp.Value.Users.Count);

            return (output, null);
        }

        private static async Task<HttpResponseMessage> SendWithRetryAsync(
            HttpClient client, Func<HttpRequestMessage> requestFactory, CancellationToken ct)
        {
            int attempts = 0;
            int throttleAttempts = 0;
            while (true)
            {
                var request = requestFactory();
                var response = await client.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (++throttleAttempts > 10) return response; // give up after 10 throttle retries
                    int wait = 60;
                    if (response.Headers.TryGetValues("Retry-After", out var vals) &&
                        int.TryParse(vals.First(), out int ra)) wait = ra;
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                    continue;
                }
                if ((int)response.StatusCode is 503 or 504)
                {
                    if (++attempts > 3) return response;
                    await Task.Delay(TimeSpan.FromSeconds(attempts == 1 ? 5 : attempts == 2 ? 15 : 30), ct);
                    continue;
                }
                return response;
            }
        }
    }
}
