using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Discovery.M1;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PnP.Scanning.Core.Tests.Discovery.M1;

// Test infrastructure only. Every attempt runs the real acquisition and both real SQLite writers.
internal sealed class ClassicAspxStreamingFixture : IAsyncDisposable
{
    internal readonly Guid Scan = Guid.NewGuid(), Site = Guid.NewGuid(), Web = Guid.NewGuid(), List = Guid.NewGuid(), File = Guid.NewGuid();
    internal static readonly Uri Url = new("https://example.invalid/sites/large-stream");
    internal const string RendererPath = "/sites/large-stream/Lists/items/DispForm.aspx";
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private DbContextOptions<ScanContext> options;
    internal int LargestPublishedBatch, PublishedBatches;
    internal ScanContext Open() => new(options);
    internal async Task InitializeAsync()
    {
        await connection.OpenAsync();
        options = new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options;
        using var db = Open();
        await db.Database.MigrateAsync();
    }
    internal async Task RunAsync(OnDemandItems client, bool singlePageCap = false)
    {
        var raw = new AssessmentDiscoveryWriter(Open);
        var projection = new ClassicAspxProjectionWriter(Open);
        var context = new M1WebAcquisitionContext(Scan, Site, Web, Url, Url, new ClassicOptions { Pages = true },
            BatchSize: 128, MaxCollectionPages: singlePageCap ? 1 : 100);
        await new ClassicAspxWebAcquisition(context, client).AcquireAsync(EmptyPhysical(), async (batch, ct) =>
        {
            LargestPublishedBatch = Math.Max(LargestPublishedBatch, batch.Inventory.Count + batch.Bindings.Count + batch.Evidence.Count);
            PublishedBatches++;
            await raw.WriteAsync(batch.Evidence, ct);
            await projection.WriteAsync(batch.Inventory, batch.Bindings, ct);
        });
    }
    private static async IAsyncEnumerable<ClassicPageDiscovery> EmptyPhysical()
    { await Task.CompletedTask; yield break; }
    public ValueTask DisposeAsync() => connection.DisposeAsync();

    internal OnDemandItems Client(string attempt, bool overrideForm = false, bool denySecondItemPage = false,
        bool truncatedSource = false) => new(List, File, attempt, overrideForm, denySecondItemPage, truncatedSource);

    internal sealed class OnDemandItems(Guid list, Guid file, string attempt, bool overrideForm,
        bool denySecondItemPage, bool truncatedSource) : ISharePointAspxRestClient
    {
        internal const int ItemCount = 10000, PageSize = 200;
        internal int ResolveCalls, SourceCalls, ItemPageRequests, GeneratedItems, LargestGeneratedPage;
        private string firstItemsUri;
        public Uri WebUrl => Url;
        public Task<SharePointRestPage> GetPageAsync(Uri uri, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
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
        private static SharePointRestPage Json(Uri uri, object[] rows, string next = null) =>
            SharePointRestResponseParser.Parse(uri, HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, object> { ["value"] = rows, ["@odata.nextLink"] = next }), "application/json");
        public Task<SharePointResolvedFile> ResolveFileAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); ResolveCalls++;
            if (path != RendererPath) throw new InvalidOperationException("Unexpected renderer.");
            return Task.FromResult(new SharePointResolvedFile(DiscoveryTerminalOutcome.Complete, file.ToString("D"),
                "DispForm.aspx", path, null, "synthetic-metadata:" + attempt));
        }
        public Task<SharePointFileContent> ReadFileContentAsync(string path, int maxBytes, TimeSpan timeout,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); SourceCalls++;
            return Task.FromResult(new SharePointFileContent(truncatedSource ? DiscoveryTerminalOutcome.Truncated : DiscoveryTerminalOutcome.Complete,
                Encoding.UTF8.GetBytes("<%@ Page Inherits='Example.SharedRenderer' %>"), !truncatedSource,
                AspxHandlerSourceKind.ActualFileBytes, truncatedSource ? AspxHandlerReadState.Truncated : AspxHandlerReadState.Available,
                "synthetic-source:" + attempt, truncatedSource ? "synthetic_source_truncated" : null, SameAuthorityConfirmed: true));
        }
        public Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SharePointModeledFolderResult> ReadFolderAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
