using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Discovery.Aspx;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.Aspx;

public sealed class ClassicAspxWebAcquisitionTests
{
    private static readonly Guid Scan = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Site = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Web = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid List = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid Renderer = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Uri SiteUrl = new("https://tenant.invalid/sites/fixture");
    private static readonly Uri WebUrl = new("https://tenant.invalid/sites/fixture/child");
    private const string Form = "/sites/fixture/child/Lists/hidden/DispForm.aspx";

    /// <summary>
    /// Builds a pages-enabled acquisition context with a configurable publication bound.
    /// </summary>
    private static ClassicAspxWebAcquisitionContext Context(int batchSize = 7, bool homeOnly = false) =>
        new(Scan, Site, Web, SiteUrl, WebUrl, new ClassicOptions { Pages = true, HomePageOnly = homeOnly },
            BatchSize: batchSize, MaxCollectionPages: 8);
    /// <summary>
    /// Yields caller-provided physical discoveries in order without adding synthetic page or item observations.
    /// </summary>
    private static async IAsyncEnumerable<ClassicPageDiscovery> Observations(params ClassicPageDiscovery[] rows)
    { foreach (var row in rows) yield return row; await Task.CompletedTask; }
    /// <summary>
    /// Supplies authenticated actual-source evidence while allowing tests to mark the same bytes explicitly denied.
    /// </summary>
    private static SharePointFileContent Bytes(AspxHandlerReadState read = AspxHandlerReadState.Available) =>
        new(read == AspxHandlerReadState.Denied ? DiscoveryTerminalOutcome.Denied : DiscoveryTerminalOutcome.Complete,
            Encoding.UTF8.GetBytes("<%@ Page Inherits='Example.ActualHandler' %>"), true,
            AspxHandlerSourceKind.ActualFileBytes, read, "FakeAuthenticatedAPI:$value", SameAuthorityConfirmed: true);

    private sealed class Client : ISharePointAspxRestClient
    {
        internal Uri ActualWebUrl = ClassicAspxWebAcquisitionTests.WebUrl;
        public Uri WebUrl => ActualWebUrl;
        internal Func<string, SharePointFileContent> Content = _ => Bytes();
        internal readonly List<Uri> Requests = new();
        internal readonly List<string> Resolutions = new();
        internal int ContentReads;
        internal readonly List<string> ContentPaths = new();
        /// <summary>
        /// Rejects collection requests so item enumeration cannot silently return an empty page.
        /// </summary>
        public Task<SharePointRestPage> GetPageAsync(Uri uri, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Requests.Add(uri); throw new InvalidOperationException("Unexpected collection request."); }
        /// <summary>
        /// Rejects file resolution because all inventory rows must come from physical observations.
        /// </summary>
        public Task<SharePointResolvedFile> ResolveFileAsync(string path, CancellationToken ct = default)
        { Resolutions.Add(path); throw new InvalidOperationException("Unexpected file resolution."); }
        /// <summary>
        /// Counts and records source reads before returning the scenario-specific complete or failed content evidence.
        /// </summary>
        public Task<SharePointFileContent> ReadFileContentAsync(string path, int maxBytes, TimeSpan timeout, CancellationToken ct = default)
        { ContentReads++; ContentPaths.Add(path); return Task.FromResult(Content(path)); }
        /// <summary>
        /// Fails unexpected welcome-page API access because acquisition scenarios provide their own physical observations.
        /// </summary>
        public Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken ct = default) => throw new NotSupportedException();
        /// <summary>
        /// Fails unexpected folder API access so tests cannot silently depend on an unmodeled discovery surface.
        /// </summary>
        public Task<SharePointModeledFolderResult> ReadFolderAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        /// <summary>
        /// Releases no external resources because this fake client only records in-memory calls.
        /// </summary>
        public void Dispose() { }
    }
    /// <summary>
    /// Returns the supplied root client and records factory requests and disposal without taking ownership of the fake client.
    /// </summary>
    private sealed class Factory(Client root) : ISharePointAspxRestClientFactory
    {
        internal readonly List<Uri> Requests = new();
        internal bool Disposed;
        /// <summary>
        /// Records root-client requests, checks cancellation, and returns the configured client for scope-validation tests.
        /// </summary>
        public Task<ISharePointAspxRestClient> GetAsync(Uri web, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Requests.Add(web); return Task.FromResult<ISharePointAspxRestClient>(root); }
        /// <summary>
        /// Records factory disposal so tests can detect accidental disposal of a caller-owned root-client factory.
        /// </summary>
        public void Dispose() => Disposed = true;
    }

    private sealed class Collected
    {
        internal readonly List<ClassicAspxInventory> Inventory = new();
        internal readonly List<ClassicAspxItemBinding> Bindings = new();
        internal readonly List<ClassicPageDiscovery> Evidence = new();
        internal int LargestBatch;
        /// <summary>
        /// Collects all three publication streams and records the largest combined batch without altering emitted facts.
        /// </summary>
        internal Task Publish(ClassicAspxAcquisitionBatch batch, CancellationToken ct)
        {
            LargestBatch = Math.Max(LargestBatch, batch.Inventory.Count + batch.Bindings.Count + batch.Evidence.Count);
            Inventory.AddRange(batch.Inventory); Bindings.AddRange(batch.Bindings); Evidence.AddRange(batch.Evidence);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Collection APIs must never be reached even when a list would return many items.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_acquisition_skips_list_item_enumeration_and_marks_count_unknown(bool homeOnly)
    {
        var client = new Client();
        var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(homeOnly: homeOnly), client)
            .AcquireAsync(Observations(), found.Publish);

        Assert.Empty(client.Requests);
        Assert.Empty(client.Resolutions);
        Assert.Equal(0, client.ContentReads);
        Assert.Empty(found.Inventory);
        Assert.Empty(found.Bindings);
        var marker = Assert.Single(found.Evidence);
        Assert.Equal("Scope", marker.RowType);
        Assert.Equal("ItemEnumerationPolicy", marker.ScopeType);
        Assert.Equal("PolicyExcluded", marker.DiscoveryStatus);
        using var details = JsonDocument.Parse(marker.EvidenceJson);
        Assert.Equal("NotCollected", details.RootElement.GetProperty("itemPages").GetString());
        Assert.Equal("NotCollected", details.RootElement.GetProperty("itemBindings").GetString());
        Assert.Equal("MetadataDeclarationsOnly", details.RootElement.GetProperty("displayForms").GetString());
    }

    /// <summary>
    /// A physical ASPX stays in inventory without making its list item a binding.
    /// </summary>
    [Fact]
    public async Task Physical_aspx_is_inventoried_without_item_binding()
    {
        var client = new Client();
        var found = new Collected();
        var observation = new ClassicPageDiscovery
        {
            ScanId = Scan, RowType = "Page", ScopeType = "File", SiteCollectionId = Site,
            WebId = Web, ListId = List, ListItemId = 7, FileUniqueId = Renderer,
            Url = Form, FileName = "DispForm.aspx", ObservationMethod = "fixture"
        };
        await new ClassicAspxWebAcquisition(Context(), client).AcquireAsync(Observations(observation), found.Publish);

        Assert.Empty(client.Requests);
        Assert.Empty(client.Resolutions);
        Assert.Equal(1, client.ContentReads);
        Assert.Empty(found.Bindings);
        var physical = Assert.Single(found.Inventory);
        Assert.Equal(ClassicAspxIdentityKinds.TenantFile, physical.IdentityKind);
        Assert.Equal(Renderer, physical.FileUniqueId);
        Assert.Equal("Example.ActualHandler", physical.HandlerEffectiveType);
        Assert.Null(physical.WebId);
        Assert.Contains(found.Evidence, e => e.ScopeType == "ItemEnumerationPolicy");
    }

    /// <summary>
    /// A denied source read still retains the observed physical file without guessing its handler.
    /// </summary>
    [Fact]
    public async Task Denied_physical_source_retains_inventory_without_handler()
    {
        var client = new Client { Content = _ => Bytes(AspxHandlerReadState.Denied) };
        var found = new Collected();
        var observation = new ClassicPageDiscovery
        {
            ScanId = Scan, RowType = "Page", ScopeType = "File", SiteCollectionId = Site,
            WebId = Web, FileUniqueId = Renderer, Url = Form, FileName = "DispForm.aspx"
        };
        await new ClassicAspxWebAcquisition(Context(), client).AcquireAsync(Observations(observation), found.Publish);

        var physical = Assert.Single(found.Inventory);
        Assert.Equal("Denied", physical.HandlerReadState);
        Assert.Null(physical.HandlerEffectiveType);
        Assert.Empty(found.Bindings);
        Assert.Empty(client.Requests);
    }

    /// <summary>
    /// Reads an observed parent Web file through the site root while keeping item binding uncollected.
    /// </summary>
    [Fact]
    public async Task Parent_physical_aspx_uses_site_root_without_item_lookup()
    {
        const string layout = "/sites/fixture/_catalogs/masterpage/shared.aspx";
        var client = new Client();
        var root = new Client { ActualWebUrl = SiteUrl };
        var factory = new Factory(root);
        var found = new Collected();
        var observation = new ClassicPageDiscovery
        {
            ScanId = Scan, RowType = "Page", ScopeType = "File", SiteCollectionId = Site,
            WebId = Web, FileUniqueId = Renderer, Url = layout, FileName = "shared.aspx"
        };
        await new ClassicAspxWebAcquisition(Context(), client, factory)
            .AcquireAsync(Observations(observation), found.Publish);

        Assert.Empty(client.Requests);
        Assert.Empty(root.Requests);
        Assert.Equal(0, client.ContentReads);
        Assert.Equal(1, root.ContentReads);
        Assert.Single(factory.Requests);
        Assert.Empty(found.Bindings);
        Assert.Null(Assert.Single(found.Inventory).WebId);
    }

    /// <summary>
    /// Persistence failures propagate while collection access remains disabled.
    /// </summary>
    [Fact]
    public async Task Storage_callback_failure_is_not_swallowed_as_a_network_gap()
    {
        var client = new Client();
        await Assert.ThrowsAsync<IOException>(() => new ClassicAspxWebAcquisition(Context(batchSize: 1), client)
            .AcquireAsync(Observations(), (_, _) => throw new IOException("synthetic storage failure")));
        Assert.Empty(client.Requests);
    }

    /// <summary>
    /// Rejects redirects, response-URI drift, and oversized JSON responses after one send without returning successful empty pages.
    /// </summary>
    [Theory]
    [InlineData("redirect")]
    [InlineData("drift")]
    [InlineData("size")]
    public async Task Bounded_json_GET_never_follows_redirect_or_accepts_unbounded_page(string fault)
    {
        var handler = new Handler((r, _) =>
        {
            var response = Reply(r, fault == "redirect" ? HttpStatusCode.Found : HttpStatusCode.OK,
                Encoding.UTF8.GetBytes("{\"value\":[]}"), "application/json");
            if (fault == "drift") response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://foreign.invalid/_api/web/lists");
            if (fault == "size") response.Content.Headers.ContentLength = 100;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var page = await PnPContextSharePointAspxRestClient.ReadBoundedPageAsync(http, WebUrl,
            new Uri(WebUrl + "/_api/web/lists"), Authenticate, maxBytes: 64);
        Assert.True(page.Outcome is DiscoveryTerminalOutcome.Unknown or DiscoveryTerminalOutcome.Truncated);
        Assert.Empty(page.Items); Assert.Equal(1, handler.Sends);
    }

    /// <summary>
    /// Applies the JSON deadline through body consumption and records timeout truncation rather than empty success.
    /// </summary>
    [Fact]
    public async Task Bounded_json_deadline_includes_body_and_preserves_no_empty_success()
    {
        var handler = new Handler((r, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = r, Content = new SlowBody() };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var page = await PnPContextSharePointAspxRestClient.ReadBoundedPageAsync(http, WebUrl,
            new Uri(WebUrl + "/_api/web/lists"), Authenticate, timeout: TimeSpan.FromMilliseconds(30));
        Assert.Equal(DiscoveryTerminalOutcome.Truncated, page.Outcome);
        Assert.Equal("json_response_timeout", page.ErrorCode); Assert.Empty(page.Items);
    }

    private sealed class SlowBody : HttpContent
    {
        /// <summary>
        /// Leaves response length unknown so bounded readers must enforce their limits while consuming the stream.
        /// </summary>
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        /// <summary>
        /// Rejects serialization because this fake content is consumed only through its deliberately stalled read stream.
        /// </summary>
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => throw new NotSupportedException();
        /// <summary>
        /// Creates the cancellation-aware stalled stream used to test deadlines after headers have already succeeded.
        /// </summary>
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new SlowRead());
        private sealed class SlowRead : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            /// <summary>
            /// Rejects flushing because the stalled body is a read-only asynchronous test stream.
            /// </summary>
            public override void Flush() => throw new NotSupportedException();
            /// <summary>
            /// Rejects synchronous reads so deadline tests must use the cancellation-aware asynchronous path.
            /// </summary>
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            /// <summary>
            /// Waits indefinitely until the reader cancels, isolating whether its deadline covers body consumption.
            /// </summary>
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            { await Task.Delay(Timeout.Infinite, ct); return 0; }
            /// <summary>
            /// Rejects seeking because the fake response body is forward-only.
            /// </summary>
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            /// <summary>
            /// Rejects resizing because the response stream exposes no writable storage.
            /// </summary>
            public override void SetLength(long value) => throw new NotSupportedException();
            /// <summary>
            /// Rejects writes because the response body exists only to stall asynchronous reads.
            /// </summary>
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Requires a successful binary header response with a stalled body to time out as incomplete source after one request.
    /// </summary>
    [Fact]
    public async Task The_same_deadline_bounds_body_after_successful_headers()
    {
        var handler = new Handler((r, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = r, Content = new SlowBody() };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var result = await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromMilliseconds(30), Authenticate);
        Assert.Equal("file_source_timeout", result.ErrorCode);
        Assert.Equal(AspxHandlerReadState.Truncated, result.ReadState);
        Assert.False(result.IsComplete); Assert.Equal(1, handler.Sends);
    }

    /// <summary>
    /// Runs a supplied in-memory HTTP scenario and counts sends so redirect and timeout tests can enforce request boundaries.
    /// </summary>
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Sends;
        /// <summary>
        /// Counts HTTP sends before forwarding the exact request and cancellation token to the scenario callback.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Sends++; return send(request, ct); }
    }
    /// <summary>
    /// Creates a response tied to the original request with caller-selected status, raw bytes, and optional media type.
    /// </summary>
    private static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode status, byte[] bytes, string media = "application/octet-stream")
    {
        var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        if (media != null) response.Content.Headers.ContentType = new MediaTypeHeaderValue(media);
        return response;
    }
    /// <summary>
    /// Adds a synthetic bearer header after checking cancellation, without accessing any live authentication service.
    /// </summary>
    private static Task Authenticate(HttpRequestMessage request, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-only"); return Task.CompletedTask; }

    /// <summary>
    /// Requires an authenticated raw-value request and complete same-authority bytes before the parser can infer a source-level fallback.
    /// </summary>
    [Fact]
    public async Task Binary_reader_uses_authenticated_value_api_and_strict_complete_source()
    {
        var handler = new Handler((r, _) => { Assert.Equal("Bearer", r.Headers.Authorization.Scheme); Assert.EndsWith("/$value", r.RequestUri.AbsolutePath); return Task.FromResult(Reply(r, HttpStatusCode.OK, Encoding.UTF8.GetBytes("<%@ Page %>"))); });
        using var http = new HttpClient(handler);
        var result = await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromSeconds(1), Authenticate);
        Assert.True(result.IsComplete); Assert.True(result.SameAuthorityConfirmed); Assert.Equal(AspxHandlerSourceKind.ActualFileBytes, result.Source);
        Assert.Equal(AspxHandlerParseState.NoInherits, AspxHandlerParser.Parse(result.Bytes, result.Source, result.IsComplete, result.ReadState).ParseState);
        Assert.Equal(1, handler.Sends);
    }

    /// <summary>
    /// Refuses handler defaults for authentication failures, missing files, partial responses, and redirects despite plausible body bytes.
    /// </summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(206)]
    [InlineData(302)]
    public async Task Binary_http_failures_partial_and_redirect_are_never_defaultable(int status)
    {
        var handler = new Handler((r, _) => Task.FromResult(Reply(r, (HttpStatusCode)status, Encoding.UTF8.GetBytes("<%@ Page %>"))));
        using var http = new HttpClient(handler);
        var result = await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromSeconds(1), Authenticate);
        Assert.False(result.IsComplete); Assert.NotEqual(AspxHandlerSourceKind.ActualFileBytes, result.Source);
        Assert.Null(AspxHandlerParser.Parse(result.Bytes, result.Source, result.IsComplete, result.ReadState).EffectiveInherits);
        Assert.Equal(1, handler.Sends);
    }

    /// <summary>
    /// Prevents rendered, JSON, or untyped bodies from being promoted to actual-source evidence or a framework default.
    /// </summary>
    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData(null)]
    public async Task Rendered_or_unproven_media_never_becomes_a_framework_default(string media)
    {
        var handler = new Handler((r, _) => Task.FromResult(Reply(r, HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html>rendered</html>"), media)));
        using var http = new HttpClient(handler);
        var result = await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromSeconds(1), Authenticate);
        Assert.NotEqual(AspxHandlerSourceKind.ActualFileBytes, result.Source);
        Assert.Null(AspxHandlerParser.Parse(result.Bytes, result.Source, result.IsComplete, result.ReadState).EffectiveInherits);
    }

    /// <summary>
    /// Enforces the byte cap and declared body length, retaining truncation or incomplete-transfer diagnostics.
    /// </summary>
    [Fact]
    public async Task Binary_size_and_length_completion_are_enforced()
    {
        var handler = new Handler((r, _) => Task.FromResult(Reply(r, HttpStatusCode.OK, new byte[33])));
        using var http = new HttpClient(handler);
        var result = await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 32, TimeSpan.FromSeconds(1), Authenticate);
        Assert.Equal(DiscoveryTerminalOutcome.Truncated, result.Outcome); Assert.Empty(result.Bytes);
        var wrong = new Handler((r, _) => { var reply = Reply(r, HttpStatusCode.OK, new byte[8]); reply.Content.Headers.ContentLength = 9; return Task.FromResult(reply); });
        using var http2 = new HttpClient(wrong);
        Assert.Equal("file_source_incomplete_body", (await SharePointFileContentReader.ReadAsync(http2, WebUrl, Form, 32, TimeSpan.FromSeconds(1), Authenticate)).ErrorCode);
    }

    /// <summary>
    /// Rejects response-URI drift and foreign locators without following another authority or issuing a second request.
    /// </summary>
    [Fact]
    public async Task Response_uri_drift_and_foreign_paths_are_not_followed_or_sent()
    {
        var handler = new Handler((r, _) => { var response = Reply(r, HttpStatusCode.OK, new byte[0]); response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://foreign.invalid/error"); return Task.FromResult(response); });
        using var http = new HttpClient(handler);
        Assert.Equal("file_source_redirect_or_uri_drift", (await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromSeconds(1), Authenticate)).ErrorCode);
        await Assert.ThrowsAsync<ArgumentException>(() => SharePointFileContentReader.ReadAsync(http, WebUrl, "//foreign.invalid/a.aspx", 1024, TimeSpan.FromSeconds(1), Authenticate));
        Assert.Equal(1, handler.Sends);
    }

    /// <summary>
    /// Includes authentication in the total read deadline and lets caller cancellation propagate without an HTTP send.
    /// </summary>
    [Fact]
    public async Task One_deadline_also_bounds_auth_and_caller_cancellation_propagates()
    {
        var handler = new Handler((r, _) => Task.FromResult(Reply(r, HttpStatusCode.OK, new byte[0])));
        using var http = new HttpClient(handler);
        var result = await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromMilliseconds(20),
            async (_, ct) => await Task.Delay(Timeout.Infinite, ct));
        Assert.Equal("file_source_timeout", result.ErrorCode); Assert.Equal(0, handler.Sends);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromSeconds(1), Authenticate, canceled.Token));
    }
}
