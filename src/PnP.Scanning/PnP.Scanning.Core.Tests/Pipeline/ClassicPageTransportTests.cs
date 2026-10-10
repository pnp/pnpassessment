#nullable enable
using System.Net;
using System.Text;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Collection;
using PnP.Scanning.Core.Pipeline.Contracts;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Discovery;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

[Trait("Category", "ClassicPagePipeline")]
public sealed class ClassicPageTransportTests
{
    [Fact]
    public async Task Discovery_transport_reopens_and_replays_original_modeled_and_REST_responses()
    {
        using var data = new StoreCase("classicpage-discovery-transport"); var seed = await data.UnsealedAsync([]);
        var context = new CollectionContext(ClassicPageFixture.MakeOptions(), VersionedJson.Empty, null, new ForbiddenOnlineEnvironment(), seed.Assessment,
            data.Store.CollectionJournal(seed.Assessment, seed.Snapshot, seed.Run));
        var journal = new ClassicPageAcquisitionJournal(context); await journal.InitializeAsync(default);
        var original = new DiscoveryClient();
        using var factory = new ClassicPageDiscoveryJournal(original, journal);
        var client = await factory.GetAsync(new Uri(ClassicPageFixture.Site));
        var uri = new Uri(ClassicPageFixture.Site + "/_api/web/lists?$top=1");
        var page = await client.GetPageAsync(uri); var welcome = await client.ReadWelcomePageAsync();
        var folder = await client.ReadFolderAsync("/sites/a/SitePages"); var resolved = await client.ResolveFileAsync("/sites/a/SitePages/Home.aspx");
        Assert.Equal(4, original.Requests);
        var resumed = new ClassicPageAcquisitionJournal(context); await resumed.InitializeAsync(default);
        using var replayFactory = new ClassicPageDiscoveryJournal(new DiscoveryClient { Forbidden = true }, resumed);
        var replay = await replayFactory.GetAsync(new Uri(ClassicPageFixture.Site));
        Assert.Equal(page.ResponseDigest, (await replay.GetPageAsync(uri)).ResponseDigest);
        Assert.Equal(page.ReceivedAtUtc, (await replay.GetPageAsync(uri)).ReceivedAtUtc);
        Assert.Equal(page.Items[0].GetRawText(), (await replay.GetPageAsync(uri)).Items[0].GetRawText());
        Assert.Equal(welcome, await replay.ReadWelcomePageAsync()); Assert.Equal(folder.Outcome, (await replay.ReadFolderAsync("/sites/a/SitePages")).Outcome);
        Assert.Equal(resolved, await replay.ResolveFileAsync("/sites/a/SitePages/Home.aspx"));
        Assert.Equal(4, (await context.Journal!.ReadCommittedAsync(default)).Count);
    }
    [Fact]
    public async Task Actual_audit_collector_resumes_original_query_and_pagination_from_SQLite_receipts()
    {
        using var data = new StoreCase("classicpage-audit-transport"); var seed = await data.UnsealedAsync([]);
        var options = ClassicPageFixture.MakeOptions(usage: true); options.Threads = 1;
        var context = new CollectionContext(options, VersionedJson.Empty, null, new ForbiddenOnlineEnvironment(), seed.Assessment,
            data.Store.CollectionJournal(seed.Assessment, seed.Snapshot, seed.Run));
        var journal = new ClassicPageAcquisitionJournal(context); await journal.InitializeAsync(default);
        var end = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var scope = new ClassicPageScopeSource([ClassicPageFixture.Site], [], true, new(false, false, true, false, 2), end.AddDays(-2), end, []);
        var handler = new AuditHandler(); using var client = new HttpClient(handler);
        var collector = new ClassicPageAuditCollector(options, null!, client, _ => Task.FromResult("fixture-token"), TimeSpan.Zero);
        using var cancel = new CancellationTokenSource();
        var first = collector.CollectAsync(scope, journal, cancel.Token);
        await handler.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, handler.SubmitCalls); Assert.Equal(1, handler.PollCalls); Assert.Equal(1, handler.FirstPageCalls);
        // Reopen the store and journal: no in-memory request cache is shared with the resumed execution.
        var reopened = new global::PnP.Scanning.Core.Storage.Pipeline.PipelineStore(data.DirectoryPath);
        var resumed = new ClassicPageAcquisitionJournal(context with { Journal = reopened.CollectionJournal(seed.Assessment, seed.Snapshot, seed.Run) });
        await resumed.InitializeAsync(default); handler.BlockSecondPage = false;
        await collector.CollectAsync(scope, resumed, default);
        Assert.Equal(1, handler.SubmitCalls); Assert.Equal(1, handler.PollCalls); Assert.Equal(1, handler.FirstPageCalls);
        Assert.Equal(2, handler.SecondPageCalls);
        var records = await context.Journal!.ReadCommittedAsync(default);
        var pages = records.Where(x => ClassicPageSourceJson.Kind(x) == "AuditPage").Select(x => ClassicPageSourceJson.Read<ClassicPageAuditPageSource>(x.RawBytes)).OrderBy(x => x.Page).ToArray();
        Assert.Equal(2, pages.Length); Assert.All(pages, x => Assert.Equal("query-original", x.QueryId));
        Assert.DoesNotContain(records, x => x.SourceIdentity.StartsWith("AuditRecordsResponse:"));
        Assert.Equal("succeeded", ClassicPageSourceJson.Read<ClassicPageAuditChunkSource>(Assert.Single(records, x => ClassicPageSourceJson.Kind(x) == "AuditChunk").RawBytes).Status);
        using var submitted = JsonDocument.Parse(handler.SubmittedBody!);
        Assert.Equal("sharePoint", submitted.RootElement.GetProperty("recordTypeFilters")[0].GetString());
        Assert.Equal(ClassicPageFixture.Site + "/*", submitted.RootElement.GetProperty("objectIdFilters")[0].GetString());
        await seed.Writer.SealAsync(default);
    }

    [Fact]
    public async Task Collection_Csom_transport_propagates_cancellation_without_changing_shared_http_client()
    {
        var pending = new PendingHandler(); using var shared = new HttpClient(pending);
        using var collection = new CancellationTokenSource();
        using var client = new HttpClient(new CollectionHttpClient(shared, collection.Token));
        var request = new HttpRequestMessage(HttpMethod.Post, "https://contoso.sharepoint.com/_vti_bin/client.svc/ProcessQuery")
        { Content = new StringContent("<Request />") };
        var operation = client.SendAsync(request);
        await pending.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20)); collection.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal("<Request />", pending.Body);
        // Disposing the collection wrapper does not own/dispose the shared client.
        client.Dispose(); pending.Complete = true;
        using var response = await shared.GetAsync("https://contoso.sharepoint.com/"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class AuditHandler : HttpMessageHandler
    {
        internal int SubmitCalls, PollCalls, FirstPageCalls, SecondPageCalls;
        internal bool BlockSecondPage = true;
        internal string? SubmittedBody;
        internal TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme); Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
            if (request.Method == HttpMethod.Post)
            { SubmitCalls++; SubmittedBody = await request.Content!.ReadAsStringAsync(token); return Response("{\"id\":\"query-original\"}"); }
            if (!request.RequestUri!.AbsolutePath.EndsWith("/records", StringComparison.Ordinal))
            { PollCalls++; return Response("{\"status\":\"succeeded\"}"); }
            if (request.RequestUri.Query.Contains("$skip"))
            {
                SecondPageCalls++;
                if (BlockSecondPage) { Reached.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                return Response("{\"value\":[]}");
            }
            FirstPageCalls++;
            return Response("{\"value\":[],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/security/auditLog/queries/query-original/records?$skip=5000\"}");
        }
        private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
    private sealed class PendingHandler : HttpMessageHandler
    {
        internal bool Complete; internal string? Body;
        internal TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Complete) return new(HttpStatusCode.OK);
            Body = await request.Content!.ReadAsStringAsync(token); Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK);
        }
    }
    private sealed class DiscoveryClient : ISharePointAspxRestClientFactory, ISharePointAspxRestClient
    {
        internal bool Forbidden; internal int Requests;
        public Uri WebUrl => new(ClassicPageFixture.Site);
        public Task<ISharePointAspxRestClient> GetAsync(Uri webUrl, CancellationToken token = default) => Task.FromResult<ISharePointAspxRestClient>(this);
        private void Count() { if (Forbidden) throw new InvalidOperationException("Completed discovery request repeated online"); Requests++; }
        public Task<SharePointRestPage> GetPageAsync(Uri request, CancellationToken token = default)
        {
            Count(); return Task.FromResult(new SharePointRestPage(request, HttpStatusCode.OK, [JsonSerializer.SerializeToElement(new { Id = ClassicPageFixture.ListId })],
                null!, "original-response-digest", "odata", DiscoveryTerminalOutcome.Complete, ReceivedAtUtc: DateTimeOffset.UtcNow));
        }
        public Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken token = default)
        { Count(); return Task.FromResult(new SharePointModeledValue(DiscoveryTerminalOutcome.Complete, "SitePages/Home.aspx", "CSOM", "WelcomePage", null!, "original-welcome", DateTimeOffset.UtcNow)); }
        public Task<SharePointModeledFolderResult> ReadFolderAsync(string url, CancellationToken token = default)
        { Count(); return Task.FromResult(new SharePointModeledFolderResult(DiscoveryTerminalOutcome.Denied, null!, url, [], [], "CSOM", "ReadFolder", "Denied", "original-folder", DateTimeOffset.UtcNow)); }
        public Task<SharePointResolvedFile> ResolveFileAsync(string url, CancellationToken token = default)
        { Count(); return Task.FromResult(new SharePointResolvedFile(DiscoveryTerminalOutcome.Complete, ClassicPageFixture.WebId.ToString(), "Home.aspx", url, "Customized", "original-file", ListId: ClassicPageFixture.ListId, ListItemId: 1)); }
        public void Dispose() { }
    }
}
