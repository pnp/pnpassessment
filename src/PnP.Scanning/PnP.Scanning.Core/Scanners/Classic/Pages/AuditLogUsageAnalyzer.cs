using PnP.Scanning.Core.Storage;
using System.Net;
using System.Text.Json;
using System.Collections.Generic;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Queries the Microsoft Graph <c>security/auditLog/queries</c> (v1.0) API for classic
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
    ///   2. Submit all sub-window queries in parallel (POST /v1.0/security/auditLog/queries)
    ///   3. Poll all queries in parallel every PollInterval until all reach "succeeded"
    ///   4. Fetch records from each query ($top=5000, follow @odata.nextLink)
    ///   5. Merge results — sum counts per pageUrl across chunks; union user-hash sets for exact UniqueUsers
    ///
    /// Chunking avoids the server-side timeout that occurs when a single large query
    /// (e.g., 14 days × 100k pages) takes longer than QueryTimeout to process.
    ///
    /// UniqueUsers is exact across chunk boundaries up to the MaxTrackedUsersPerPage cap: each chunk
    /// carries raw user-hash HashSets that MergeChunks unions (de-duplicating a user seen in multiple
    /// chunks) before taking the count. Once a page's merged set reaches the cap the count saturates at
    /// MaxTrackedUsersPerPage, so per-page memory stays bounded regardless of chunk count.
    ///
    /// Record field mapping (v1.0): "operation", "objectId", "userId" are all top-level properties.
    /// The "auditData" field is a nested object and is NOT used.
    /// </summary>
    internal static class AuditLogUsageAnalyzer
    {
        internal readonly record struct AuditPageStats(int ViewsCount, int CreatesCount, int EditsCount, int UniqueUsers);

        // Intermediate type used within a single chunk's fetch result.
        // Carries raw HashSet<int> user hashes so MergeChunks can union them correctly.
        internal readonly record struct ChunkPageData(int ViewsCount, int CreatesCount, int EditsCount, HashSet<int> UserHashes);

        private static readonly HashSet<string> ViewOperations = new(StringComparer.OrdinalIgnoreCase)
            { "ClassicPageViewed" };
        private static readonly HashSet<string> CreateOperations = new(StringComparer.OrdinalIgnoreCase)
            { "ClassicPageCreated" };
        private static readonly HashSet<string> EditOperations = new(StringComparer.OrdinalIgnoreCase)
            { "ClassicPageEdited" };

        private static readonly TimeSpan PollInterval   = TimeSpan.FromSeconds(60);
        // Graph audit queries are async and can sit in "notStarted" for a long time on a busy tenant
        // before Graph even begins processing. Observed queue waits of ~50-60 min end-to-end, so a
        // shorter timeout makes an otherwise-healthy query fail. 90 min gives Graph room to drain its
        // queue while still bounding how long post-assessment can block.
        private static readonly TimeSpan QueryTimeout   = TimeSpan.FromMinutes(90);
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
        /// Returns (null, skipReason) on permission error, query failure, or timeout, where skipReason
        /// is a human-readable message (written to the CSV SkipReason column). On full success returns
        /// (stats, null); on partial success returns (merged stats, "PartialData: ..." reason).
        /// </summary>
        internal static async Task<(IReadOnlyDictionary<string, AuditPageStats> Stats, string SkipReason)> QueryAllSitesAuditUsageAsync(
            HttpClient httpClient, string graphBaseUrl, Func<CancellationToken, Task<string>> tokenProvider,
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
                        httpClient, graphBaseUrl, tokenProvider, siteUrls,
                        c.Start, c.End, cancellationToken, progress);

                    int done = System.Threading.Interlocked.Increment(ref completed);
                    progress?.Invoke($"Audit log query {done}/{total} completed ({c.Start:MM-dd} → {c.End:MM-dd})");
                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw; // genuine cancellation propagates and aborts the whole collection
                }
                catch (Exception ex)
                {
                    // Catch-all so one chunk's unexpected failure (e.g. a connection reset surfacing from
                    // ReadAsStringAsync, which is outside QueryChunkAsync's inner try/catch) faults only
                    // THIS chunk's tuple rather than the whole Task.WhenAll — preserving partial-success
                    // data from the sibling chunks that did complete.
                    return (Stats: (IReadOnlyDictionary<string, ChunkPageData>)null, SkipReason: $"ChunkError ({c.Start:MM-dd}→{c.End:MM-dd}): {ex.Message}");
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

            // One-line terminal roll-up of every chunk's outcome. When some chunks time out while others
            // succeed, the individual timeout lines are scattered ~90 min apart in the log; this collects
            // the final tally in one place so an operator immediately sees "N succeeded, M failed" and the
            // reason for each failure, rather than reconstructing it from hundreds of interleaved lines.
            if (failures.Count > 0)
                progress?.Invoke($"Audit chunk outcomes: {succeeded.Count}/{total} succeeded, {failures.Count}/{total} failed — " +
                                 string.Join(" | ", failures));

            if (succeeded.Count == 0)
            {
                // All chunks failed (or window was empty). Surface the FIRST failure's reason so the
                // caller can report it; if there were literally no chunks, treat as a benign no-op.
                if (failures.Count == 0)
                    return (null, "No chunks to query");
                return (null, failures[0]);
            }

            var merged = MergeChunks(succeeded);

            // Total attributed events across all pages (views + creates + edits). Reported alongside the
            // page count so the log answers both "how many pages had activity" and "how much activity" —
            // a chunk that returns thousands of records collapsing to a handful of pages (or vice-versa)
            // is then visible without opening the CSV.
            long totalEvents = merged.Values.Aggregate(0L, (sum, s) => sum + s.ViewsCount + s.CreatesCount + s.EditsCount);

            if (failures.Count > 0)
            {
                // Partial success: return the merged stats AND a non-null skipReason so the caller marks
                // rows "partial" rather than "succeeded", making the coverage gap visible in the CSV.
                // The reason enumerates every distinct chunk failure so an operator can see what was missed.
                string partialReason = $"PartialData: {failures.Count}/{total} chunk(s) failed — {string.Join("; ", failures.Distinct())}";
                progress?.Invoke($"WARNING: {partialReason}");
                progress?.Invoke($"Audit log collection partial: {merged.Count} page(s), {totalEvents} event(s) across {succeeded.Count}/{total} chunks");
                return (merged, partialReason);
            }

            progress?.Invoke($"Audit log collection done: {merged.Count} page(s), {totalEvents} event(s) across {succeeded.Count}/{total} chunks");
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
            IEnumerable<IReadOnlyDictionary<string, ChunkPageData>> chunks)
        {
            var merged = new Dictionary<string, (int Views, int Creates, int Edits, HashSet<int> Users)>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in chunks)
            {
                foreach (var kvp in chunk)
                {
                    if (!merged.TryGetValue(kvp.Key, out var existing))
                    {
                        // Copy so we never alias (or later mutate) the input chunk's own set.
                        // The chunk set is already ≤ MaxTrackedUsersPerPage (capped at fetch time).
                        merged[kvp.Key] = (kvp.Value.ViewsCount, kvp.Value.CreatesCount, kvp.Value.EditsCount, new HashSet<int>(kvp.Value.UserHashes));
                    }
                    else
                    {
                        // Re-apply the per-page cap on the merged (cross-chunk) set: a hot page appearing
                        // in every chunk could otherwise accumulate up to chunks × MaxTrackedUsersPerPage
                        // hashes. Stop unioning once the merged set reaches the cap — beyond that the page
                        // is clearly "heavily used" and the exact distinct count matters less, while memory
                        // stays bounded to MaxTrackedUsersPerPage × 4 bytes per page.
                        foreach (var userHash in kvp.Value.UserHashes)
                        {
                            if (existing.Users.Count >= MaxTrackedUsersPerPage) break;
                            existing.Users.Add(userHash);
                        }
                        merged[kvp.Key] = (
                            existing.Views   + kvp.Value.ViewsCount,
                            existing.Creates + kvp.Value.CreatesCount,
                            existing.Edits   + kvp.Value.EditsCount,
                            existing.Users);
                    }
                }
            }
            return merged.ToDictionary(
                kvp => kvp.Key,
                kvp => new AuditPageStats(kvp.Value.Views, kvp.Value.Creates, kvp.Value.Edits, kvp.Value.Users.Count),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Submits, polls, and fetches one sub-window Graph query.</summary>
        private static async Task<(IReadOnlyDictionary<string, ChunkPageData> Stats, string SkipReason)> QueryChunkAsync(
            HttpClient httpClient, string graphBaseUrl, Func<CancellationToken, Task<string>> tokenProvider,
            IReadOnlyList<string> siteUrls,
            DateTime chunkStart, DateTime chunkEnd,
            CancellationToken cancellationToken,
            Action<string> progress = null)
        {
            // Label a chunk by its date range so interleaved progress lines from the (up to 7)
            // parallel chunks stay distinguishable in the log.
            string chunkLabel = $"{chunkStart:MM-dd}→{chunkEnd:MM-dd}";
            string queriesUrl = $"https://{graphBaseUrl}/v1.0/security/auditLog/queries";

            // Step 1: Submit
            string queryId;
            try
            {
                var queryBody = new Dictionary<string, object>
                {
                    ["filterStartDateTime"] = chunkStart.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                    ["filterEndDateTime"]   = chunkEnd.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                    // recordTypeFilters value is the auditLogRecordType enum — case-sensitive, camelCase.
                    // "sharePoint" (lowercase s) is the documented member; "SharePoint" is not a valid
                    // enum value and gets dropped, leaving the record type unfiltered.
                    ["recordTypeFilters"]   = new[] { "sharePoint" },
                    ["operationFilters"]    = new[] { "ClassicPageViewed", "ClassicPageCreated", "ClassicPageEdited" },
                };
                // (No serviceFilter: recordTypeFilters already scopes to SharePoint, and the singular
                //  "serviceFilter" we used to send was silently ignored by the service anyway.)
                if (siteUrls != null && siteUrls.Count > 0)
                    queryBody["objectIdFilters"] = siteUrls.Select(u => u.TrimEnd('/') + "/*").ToArray();

                var body = JsonSerializer.Serialize(queryBody);

                using var postResponse = await SendWithRetryAsync(httpClient, () =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, queriesUrl);
                    req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                    return req;
                }, tokenProvider, cancellationToken);

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
            // Graph audit-log queries are async: after submit they sit at "notStarted" and can queue
            // for many minutes before Graph processes them. Without logging, that wait is invisible and
            // looks like a hang. We log the query id once on submit, then log every status transition
            // (plus a periodic heartbeat) so the operator can tell "waiting on Graph" from "stuck".
            progress?.Invoke($"[{chunkLabel}] submitted query {queryId}, polling for completion (timeout {QueryTimeout.TotalMinutes:0} min)");
            // Echo the exact filters that were sent. A query that returns 0 records because a filter is
            // subtly wrong (bad objectId prefix, wrong operation name, dropped recordType) is otherwise
            // indistinguishable from "the tenant genuinely had no activity" — this line makes the query
            // definition auditable straight from the log without re-deriving it from the source.
            {
                string objectScope = (siteUrls != null && siteUrls.Count > 0)
                    ? string.Join(", ", siteUrls.Select(u => u.TrimEnd('/') + "/*"))
                    : "(none — whole tenant)";
                progress?.Invoke($"[{chunkLabel}] query {queryId} filters: " +
                                 $"window {chunkStart:yyyy-MM-ddTHH:mm:ssZ}→{chunkEnd:yyyy-MM-ddTHH:mm:ssZ}; " +
                                 $"recordTypes=[sharePoint]; " +
                                 $"operations=[ClassicPageViewed, ClassicPageCreated, ClassicPageEdited]; " +
                                 $"objectIdFilters=[{objectScope}]");
            }
            var pollStart = DateTime.UtcNow;
            var deadline = pollStart.Add(QueryTimeout);
            string lastStatus = null;
            int pollCount = 0;
            var lastHeartbeat = pollStart;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(PollInterval, cancellationToken);

                if (DateTime.UtcNow >= deadline)
                {
                    progress?.Invoke($"[{chunkLabel}] query {queryId} timed out after {QueryTimeout.TotalMinutes:0} min (last status '{lastStatus ?? "unknown"}')");
                    return (null, $"QueryTimeout: query {queryId} did not complete within {QueryTimeout.TotalMinutes} minutes");
                }

                HttpResponseMessage pollResponse;
                try
                {
                    pollResponse = await SendWithRetryAsync(httpClient, () =>
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, $"{queriesUrl}/{queryId}");
                        return req;
                    }, tokenProvider, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return (null, $"PollError: {ex.Message}"); }

                // Read the body then dispose the response immediately — this loop can run for dozens of
                // polls (PollInterval apart, up to QueryTimeout) per chunk, so leaking one response per poll adds up.
                string status;
                using (pollResponse)
                {
                    if (!pollResponse.IsSuccessStatusCode)
                    {
                        var errBody = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
                        return (null, $"PollError: HTTP {(int)pollResponse.StatusCode}: {errBody[..Math.Min(200, errBody.Length)]}");
                    }

                    var pollBody = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
                    try
                    {
                        using var pollDoc = JsonDocument.Parse(pollBody);
                        status = pollDoc.RootElement.GetProperty("status").GetString() ?? string.Empty;
                    }
                    catch (Exception ex) { return (null, $"ParseError polling query {queryId}: {ex.Message}"); }
                }

                pollCount++;
                var elapsed = DateTime.UtcNow - pollStart;
                // Log on every status change, and otherwise a heartbeat every ~2 min so a long
                // "notStarted"/"running" wait is visibly progressing rather than silently frozen.
                if (status != lastStatus)
                {
                    progress?.Invoke($"[{chunkLabel}] query {queryId} status '{status}' after {elapsed.TotalSeconds:0}s ({pollCount} polls)");
                    lastStatus = status;
                    lastHeartbeat = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastHeartbeat >= TimeSpan.FromMinutes(2))
                {
                    progress?.Invoke($"[{chunkLabel}] query {queryId} still '{status}' after {elapsed.TotalMinutes:0.0} min");
                    lastHeartbeat = DateTime.UtcNow;
                }

                if (status == "failed" || status == "cancelled")
                    return (null, $"QueryFailed: query {queryId} reported {status} status");
                if (status == "succeeded")
                    break;
            }

            // Step 3: Fetch records ($top=5000 to minimise round trips)
            // HashSet<int> stores hash(userId) — 4 bytes/entry vs ~50 bytes for full string; bounded by MaxTrackedUsersPerPage
            var results = new Dictionary<string, (int Views, int Creates, int Edits, HashSet<int> Users)>(StringComparer.OrdinalIgnoreCase);
            string nextLink = $"{queriesUrl}/{queryId}/records?$top={PageSize}";

            // Record-fetch can page through many @odata.nextLink hops after a query succeeds. Without
            // logging here, a scan that "succeeded" then stalls (a slow/hung nextLink page, a 401 on a
            // late page) looks identical to a finished chunk — the last log line was 'succeeded' and then
            // silence. Track page/record counts so the fetch phase is visible and its throughput auditable.
            int recordsFetched = 0;
            int recordPages = 0;
            var fetchStart = DateTime.UtcNow;
            progress?.Invoke($"[{chunkLabel}] query {queryId} succeeded — fetching records");

            while (nextLink != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                HttpResponseMessage recordsResponse;
                try
                {
                    recordsResponse = await SendWithRetryAsync(httpClient, () =>
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, nextLink);
                        return req;
                    }, tokenProvider, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return (null, $"RecordsError after {recordsFetched} records ({recordPages} pages): {ex.Message}"); }

                string recordsBody;
                // Dispose the records response as soon as its body is read — a page of 5000 records can
                // require many round trips (@odata.nextLink), so hold each response no longer than needed.
                using (recordsResponse)
                {
                    if (!recordsResponse.IsSuccessStatusCode)
                    {
                        var errBody = await recordsResponse.Content.ReadAsStringAsync(cancellationToken);
                        return (null, $"RecordsError after {recordsFetched} records ({recordPages} pages): HTTP {(int)recordsResponse.StatusCode}: {errBody[..Math.Min(200, errBody.Length)]}");
                    }

                    recordsBody = await recordsResponse.Content.ReadAsStringAsync(cancellationToken);
                }

                JsonDocument recordsDoc;
                try { recordsDoc = JsonDocument.Parse(recordsBody); }
                catch (JsonException ex) { return (null, $"ParseError fetching records for query {queryId} after {recordsFetched} records ({recordPages} pages): {ex.Message}"); }

                using (recordsDoc)
                {
                if (!recordsDoc.RootElement.TryGetProperty("value", out var valueElement))
                    return (null, $"ParseError: records response for query {queryId} missing 'value' array");

                int pageRecordCount = 0;
                foreach (var record in valueElement.EnumerateArray())
                {
                    pageRecordCount++;
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

                recordPages++;
                recordsFetched += pageRecordCount;

                nextLink = recordsDoc.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkProp)
                    ? nextLinkProp.GetString() : null;

                // Log a per-page line only while there is more to fetch (nextLink != null), so a
                // single-page chunk stays quiet and only the rollup below reports it. For multi-page
                // fetches this makes forward progress visible instead of silence between pages.
                if (nextLink != null)
                    progress?.Invoke($"[{chunkLabel}] query {queryId} fetching records: {recordsFetched} so far ({recordPages} pages)");
                } // end using (recordsDoc)
            }

            progress?.Invoke($"[{chunkLabel}] query {queryId} fetched {recordsFetched} record(s) in {recordPages} page(s) " +
                             $"over {(DateTime.UtcNow - fetchStart).TotalSeconds:0}s → {results.Count} distinct page(s)");

            var output = new Dictionary<string, ChunkPageData>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in results)
                output[kvp.Key] = new ChunkPageData(kvp.Value.Views, kvp.Value.Creates, kvp.Value.Edits, kvp.Value.Users);

            return (output, null);
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
}
