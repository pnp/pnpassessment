#nullable enable
using System.Text.Json;
using System.Threading.Tasks.Dataflow;
using PnP.Core.Services;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Pipeline.Contracts;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Pipeline.Collection;

internal sealed record ClassicAuditHttpReceipt(int StatusCode, string Body, DateTime ReceivedAtUtc, string? Error = null);

/// <summary>Graph acquisition only. Completed submissions, polls and record pages survive pause/process restart.</summary>
internal sealed class ClassicPageAuditCollector(StartRequest options, AuthenticationManager authentication,
    HttpClient? client = null, Func<CancellationToken, Task<string>>? tokenProvider = null, TimeSpan? pollInterval = null)
{
    internal async Task CollectAsync(ClassicPageScopeSource scope, ClassicPageAcquisitionJournal journal, CancellationToken token)
    {
        Enum.TryParse<Microsoft365Environment>(options.Environment, out var cloud);
        var graph = CloudManager.GetMicrosoftGraphAuthority(cloud);
        var provider = tokenProvider ?? (_ => authentication.GetAccessTokenAsync(new[] { "https://" + graph + "/.default" }));
        string? skip = cloud is not (Microsoft365Environment.Production or Microsoft365Environment.PreProduction) ? "SovereignCloud" : null;
        if (skip == null)
        {
            try { await provider(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { skip = "TokenError: " + ex.Message; }
        }
        var windows = AuditLogUsageAnalyzer.SplitWindow(scope.AuditWindowStart, scope.AuditWindowEnd, 2);
        var workers = new ActionBlock<int>(async chunk =>
        {
            var (start, end) = windows[chunk];
            await journal.ReadOrAcquireAsync("AuditChunk", chunk.ToString(), async () => skip != null
                ? new ClassicPageAuditChunkSource(chunk, start, end, null, "skipped", skip)
                : await CollectChunkAsync(chunk, start, end, scope, journal, graph, provider, token), token,
                x => x.Status == "succeeded" ? AcquisitionStatus.Complete : x.Status == "skipped" ? AcquisitionStatus.NotAttempted : AcquisitionStatus.Failed,
                x => x.Error);
        }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = Math.Min(7, Math.Max(1, options.Threads)),
            BoundedCapacity = 7, CancellationToken = token });
        try { for (var index = 0; index < windows.Count; index++) if (!await workers.SendAsync(index, token)) break; }
        finally { workers.Complete(); await workers.Completion; }
    }

    private async Task<ClassicPageAuditChunkSource> CollectChunkAsync(int chunk, DateTime start, DateTime end,
        ClassicPageScopeSource scope, ClassicPageAcquisitionJournal journal, string graph,
        Func<CancellationToken, Task<string>> provider, CancellationToken token)
    {
        var endpoint = "https://" + graph + "/v1.0/security/auditLog/queries";
        string? queryId = null;
        ClassicPageAuditChunkSource Failed(string error) => new(chunk, start, end, queryId, "failed", error);
        var query = new Dictionary<string, object>
        {
            ["filterStartDateTime"] = start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["filterEndDateTime"] = end.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["recordTypeFilters"] = new[] { "sharePoint" },
            ["operationFilters"] = new[] { "ClassicPageViewed", "ClassicPageCreated", "ClassicPageEdited" },
        };
        if (scope.ExplicitSites && scope.Sites.Length > 0) query["objectIdFilters"] = scope.Sites.Select(x => x.TrimEnd('/') + "/*").ToArray();
        var submitted = await journal.ReadOrAcquireAsync("AuditSubmit", chunk.ToString(),
            () => RequestAsync(endpoint, JsonSerializer.Serialize(query), provider, token), token);
        if (submitted.Error != null || submitted.StatusCode is < 200 or >= 300)
            return Failed(submitted.StatusCode == 403 ? "NoPermission: AuditLogsQuery-SharePoint.Read.All" : "SubmitError: " + (submitted.Error ?? submitted.Body));
        try
        {
            using var doc = JsonDocument.Parse(submitted.Body);
            queryId = doc.RootElement.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(queryId)) return Failed("SubmitError: response contained no query ID");
        }
        catch (JsonException error) { return Failed("ParseError: " + error.Message); }
        var poll = 0;
        while (true)
        {
            var key = chunk + "|" + poll++;
            if (!journal.Contains("AuditPoll", key)) await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(60), token);
            var receipt = await journal.ReadOrAcquireAsync("AuditPoll", key,
                () => RequestAsync(endpoint + "/" + Uri.EscapeDataString(queryId), null, provider, token), token);
            if (receipt.Error != null || receipt.StatusCode is < 200 or >= 300) return Failed("PollError: " + (receipt.Error ?? receipt.Body));
            string status;
            try { using var doc = JsonDocument.Parse(receipt.Body); status = doc.RootElement.GetProperty("status").GetString() ?? ""; }
            catch (Exception error) when (error is JsonException or KeyNotFoundException) { return Failed("ParseError: " + error.Message); }
            Serilog.Log.Information("Audit query {QueryId}: {Status}", queryId, status);
            if (status is "failed" or "cancelled") return Failed("QueryFailed: " + status);
            if (status == "succeeded") break;
            if (receipt.ReceivedAtUtc - submitted.ReceivedAtUtc >= TimeSpan.FromMinutes(90)) return Failed("QueryTimeout: " + queryId);
        }
        string? next = endpoint + "/" + Uri.EscapeDataString(queryId) + "/records?$top=5000";
        var pageIndex = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (next != null)
        {
            if (!seen.Add(next)) return Failed("RecordsError: repeated continuation");
            if (!Uri.TryCreate(next, UriKind.Absolute, out var requestUri) || requestUri.Scheme != "https" ||
                !requestUri.Host.Equals(graph, StringComparison.OrdinalIgnoreCase) || !requestUri.AbsolutePath.StartsWith("/v1.0/security/auditLog/queries/", StringComparison.Ordinal))
                return Failed("RecordsError: continuation is outside the selected Graph authority");
            var ordinal = pageIndex++;
            var url = next;
            var response = await journal.ReadOrAcquireAsync("AuditPage", chunk + "|" + ordinal, async () =>
            {
                var receipt = await RequestAsync(url, null, provider, token);
                string? continuation = null;
                try { using var parsed = JsonDocument.Parse(receipt.Body);
                    continuation = parsed.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null; }
                catch (JsonException) { /* Persist the original body before settling its parse failure. */ }
                return new ClassicPageAuditPageSource(chunk, queryId, ordinal, receipt.Body, continuation, receipt.StatusCode, receipt.Error);
            }, token);
            if (response.Error != null || response.StatusCode is < 200 or >= 300) return Failed("RecordsError: " + (response.Error ?? response.Body));
            try
            {
                using var doc = JsonDocument.Parse(response.Body);
                if (!doc.RootElement.TryGetProperty("value", out var records) || records.ValueKind != JsonValueKind.Array)
                    return Failed("ParseError: records response has no value array");
                next = doc.RootElement.TryGetProperty("@odata.nextLink", out var continuation) ? continuation.GetString() : null;
            }
            catch (JsonException error) { return Failed("ParseError: " + error.Message); }
        }
        return new(chunk, start, end, queryId, "succeeded", null);
    }

    private async Task<ClassicAuditHttpReceipt> RequestAsync(string url, string? body, Func<CancellationToken, Task<string>> provider, CancellationToken token)
    {
        try
        {
            using var response = await AuditLogUsageAnalyzer.SendWithRetryAsync(client ?? AuthenticationManager.HttpClient, () =>
            {
                var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, url);
                if (body != null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                return request;
            }, provider, token);
            return new((int)response.StatusCode, await response.Content.ReadAsStringAsync(token), DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { return new(0, "", DateTime.UtcNow, error.GetType().Name + ": " + error.Message); }
    }
}
