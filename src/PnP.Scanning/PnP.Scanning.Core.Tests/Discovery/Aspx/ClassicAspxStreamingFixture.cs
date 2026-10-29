using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Discovery.Aspx;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PnP.Scanning.Core.Tests.Discovery.Aspx;

// Test infrastructure only. Every attempt runs the real acquisition and both real SQLite writers.
internal sealed class ClassicAspxStreamingFixture : IAsyncDisposable
{
    internal readonly Guid Scan = Guid.NewGuid(), Site = Guid.NewGuid(), Web = Guid.NewGuid(), List = Guid.NewGuid(), File = Guid.NewGuid();
    internal static readonly Uri Url = new("https://example.invalid/sites/large-stream");
    internal const string RendererPath = "/sites/large-stream/Lists/items/DispForm.aspx";
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private DbContextOptions<ScanContext> options;
    internal int LargestPublishedBatch, PublishedBatches;
    /// <summary>
    /// Opens a fresh EF context over the fixture-owned connection so acquisition and verification share one in-memory database.
    /// </summary>
    internal ScanContext Open() => new(options);
    /// <summary>
    /// Keeps the SQLite connection alive and applies real migrations before exercising acquisition and persistence.
    /// </summary>
    internal async Task InitializeAsync()
    {
        await connection.OpenAsync();
        options = new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options;
        using var db = Open();
        await db.Database.MigrateAsync();
    }
    /// <summary>
    /// Runs real acquisition and both writers, recording publication bounds.
    /// </summary>
    internal async Task RunAsync(OnDemandItems client, bool singlePageCap = false)
    {
        var raw = new AssessmentDiscoveryWriter(Open);
        var projection = new ClassicAspxProjectionWriter(Open);
        var context = new ClassicAspxWebAcquisitionContext(Scan, Site, Web, Url, Url, new ClassicOptions { Pages = true },
            BatchSize: 128, MaxCollectionPages: singlePageCap ? 1 : 100);
        await new ClassicAspxWebAcquisition(context, client).AcquireAsync(EmptyPhysical(), async (batch, ct) =>
        {
            LargestPublishedBatch = Math.Max(LargestPublishedBatch, batch.Inventory.Count + batch.Bindings.Count + batch.Evidence.Count);
            PublishedBatches++;
            await raw.WriteAsync(batch.Evidence, ct);
            await projection.WriteAsync(batch.Inventory, batch.Bindings, ct);
        });
    }
    /// <summary>
    /// Provides an empty physical-discovery stream so any item access is visible in the fake client's counters.
    /// </summary>
    private static async IAsyncEnumerable<ClassicPageDiscovery> EmptyPhysical()
    { await Task.CompletedTask; yield break; }
    /// <summary>
    /// Releases the fixture-owned SQLite connection and its in-memory database after all replay attempts.
    /// </summary>
    public ValueTask DisposeAsync() => connection.DisposeAsync();

    /// <summary>
    /// Creates an independently counted REST attempt with configurable renderer override, continuation denial, and source truncation.
    /// </summary>
    internal OnDemandItems Client(string attempt, bool overrideForm = false, bool denySecondItemPage = false,
        bool truncatedSource = false) => new(List, File, attempt, overrideForm, denySecondItemPage, truncatedSource);

    /// <summary>
    /// Generates item pages lazily for one replay attempt, with independent counters and configurable renderer, denial, and truncation faults.
    /// </summary>
    internal sealed class OnDemandItems(Guid list, Guid file, string attempt, bool overrideForm,
        bool denySecondItemPage, bool truncatedSource) : ISharePointAspxRestClient
    {
        internal const int ItemCount = 10000, PageSize = 200;
        internal int TotalPageRequests, ResolveCalls, SourceCalls, ItemPageRequests, GeneratedItems, LargestGeneratedPage;
        private string firstItemsUri;
        public Uri WebUrl => Url;
        /// <summary>
        /// Generates only the requested item page, tracks its bounds, and can deny the first continuation without materializing the full item set.
        /// </summary>
        public Task<SharePointRestPage> GetPageAsync(Uri uri, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            TotalPageRequests++;
            if (uri.AbsolutePath.EndsWith("/lists", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json(uri, new object[] { new { Id = list, BaseType = 0, Hidden = true,
                    DefaultDisplayFormUrl = RendererPath } }));
            if (uri.AbsolutePath.EndsWith("/ContentTypes", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json(uri, new object[] { new { Id = new { StringValue = "0x01" },
                    DisplayFormUrl = overrideForm ? RendererPath : "" } }));
            if (!uri.AbsolutePath.EndsWith("/items", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected synthetic API endpoint.");
            ItemPageRequests++;
            firstItemsUri ??= uri.AbsoluteUri;
            int after = 0;
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
            {
                int equal = part.IndexOf('=');
                if (equal >= 0 && WebUtility.UrlDecode(part[..equal]) == "$skiptoken")
                    after = int.Parse(WebUtility.UrlDecode(part[(equal + 1)..]), System.Globalization.CultureInfo.InvariantCulture);
            }
            if (denySecondItemPage && after == PageSize)
                return Task.FromResult(SharePointRestResponseParser.Parse(uri, HttpStatusCode.Forbidden,
                    Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"synthetic_denied\",\"message\":\"synthetic\"}}"), "application/json"));
            int count = Math.Min(PageSize, ItemCount - after);
            // Only this server page is ever generated/materialized. There is no 10k source array/list.
            var rows = new object[count];
            for (int i = 0; i < count; i++) rows[i] = new { Id = after + i + 1,
                Title = "Observed item " + (after + i + 1), ContentTypeId = "0x01" };
            GeneratedItems += count;
            LargestGeneratedPage = Math.Max(LargestGeneratedPage, count);
            string next = after + count < ItemCount ? firstItemsUri + "&$skiptoken=" + (after + count) : null;
            return Task.FromResult(Json(uri, rows, next));
        }
        /// <summary>
        /// Builds a parsed JSON collection response with an optional continuation using the same response parser as acquisition.
        /// </summary>
        private static SharePointRestPage Json(Uri uri, object[] rows, string next = null) =>
            SharePointRestResponseParser.Parse(uri, HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, object> { ["value"] = rows, ["@odata.nextLink"] = next }), "application/json");
        /// <summary>
        /// Counts metadata lookups and returns one stable file identity only for the expected shared renderer path.
        /// </summary>
        public Task<SharePointResolvedFile> ResolveFileAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); ResolveCalls++;
            if (path != RendererPath) throw new InvalidOperationException("Unexpected renderer.");
            return Task.FromResult(new SharePointResolvedFile(DiscoveryTerminalOutcome.Complete, file.ToString("D"),
                "DispForm.aspx", path, null, "synthetic-metadata:" + attempt));
        }
        /// <summary>
        /// Counts source reads and returns actual handler bytes with either complete or explicitly truncated evidence for the attempt.
        /// </summary>
        public Task<SharePointFileContent> ReadFileContentAsync(string path, int maxBytes, TimeSpan timeout,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); SourceCalls++;
            return Task.FromResult(new SharePointFileContent(truncatedSource ? DiscoveryTerminalOutcome.Truncated : DiscoveryTerminalOutcome.Complete,
                Encoding.UTF8.GetBytes("<%@ Page Inherits='Example.SharedRenderer' %>"), !truncatedSource,
                AspxHandlerSourceKind.ActualFileBytes, truncatedSource ? AspxHandlerReadState.Truncated : AspxHandlerReadState.Available,
                "synthetic-source:" + attempt, truncatedSource ? "synthetic_source_truncated" : null, SameAuthorityConfirmed: true));
        }
        /// <summary>
        /// Rejects welcome-page reads because this items-only fixture must not depend on unrelated modeled APIs.
        /// </summary>
        public Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken ct = default) => throw new NotSupportedException();
        /// <summary>
        /// Rejects folder reads so an unexpected traversal fails instead of silently expanding the synthetic scenario.
        /// </summary>
        public Task<SharePointModeledFolderResult> ReadFolderAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        /// <summary>
        /// Leaves the synthetic client with no resources to release; the surrounding fixture owns database lifetime.
        /// </summary>
        public void Dispose() { }
    }
}
