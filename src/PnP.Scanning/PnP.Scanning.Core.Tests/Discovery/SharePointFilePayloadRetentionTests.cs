using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed partial class SharePointLiveAspxDiscoveryProviderTests
{
    private readonly ITestOutputHelper output;

    public SharePointLiveAspxDiscoveryProviderTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Many_folders_release_completed_file_payloads_and_preserve_overlap_evidence(bool mixedExtensions)
    {
        const int folderCount = 20;
        const int rowsPerFolder = 500;
        var payloads = new FilePayloadProbe();
        using var factory = new FolderFactory();
        var paths = Enumerable.Range(0, folderCount).Select(index => RootPath + "/F" + index.ToString("D2")).ToArray();
        factory.Client.FolderResponse = uri =>
        {
            var path = FolderPath(uri);
            if (uri.AbsolutePath.EndsWith("/Folders", StringComparison.Ordinal))
            {
                var children = path == "/sites/a" ? new[] { FolderRow(RootPath) }
                    : path == RootPath ? paths.Select((folder, index) => JsonSerializer.SerializeToElement(new
                    {
                        UniqueId = new Guid(index + 10, 0, 0, new byte[8]),
                        Name = folder.Split('/')[^1], ServerRelativeUrl = folder,
                    })).ToArray() : Array.Empty<JsonElement>();
                return FixturePage(uri, children);
            }
            int folderIndex = Array.IndexOf(paths, path);
            return folderIndex >= 0 && QueryNumber(uri, "$skip", 0) == 0
                ? payloads.Page(uri, folderIndex * rowsPerFolder + 1, rowsPerFolder, mixedExtensions)
                : FixturePage(uri, Array.Empty<JsonElement>());
        };
        using var provider = Provider(factory);
        var pending = new Queue<DiscoveryScopeRegistration>();
        var observed = new HashSet<string>();
        int aspx = 0;
        long peakRetainedBytes = 0;
        DiscoveryScopeRegistration revisit = null;
        pending.Enqueue(provider.RootScope);
        while (pending.TryDequeue(out var scope))
        {
            if (scope.Locator == paths[0]) revisit = scope;
            if (scope.SourceKind != null)
            {
                await DrainFilesAsync(provider, scope, record =>
                {
                    observed.Add(record.FileUniqueId).Should().BeTrue();
                    record.ListId.Should().Be(ListId);
                    var folderIndex = Array.FindIndex(paths, path => record.PhysicalLocator.StartsWith(path + "/", StringComparison.Ordinal));
                    record.FolderUniqueId.Should().Be(new Guid(folderIndex + 10, 0, 0, new byte[8]));
                    if (AspxAdmission.Evaluate(record).IsAspx) aspx++;
                });
                peakRetainedBytes = Math.Max(peakRetainedBytes, payloads.CollectAndCountLiveBytes());
            }
            foreach (var child in (await provider.EnumerateChildrenAsync(scope)).ObservedChildren)
            {
                pending.Enqueue(child);
            }
        }

        observed.Should().HaveCount(folderCount * rowsPerFolder);
        aspx.Should().Be(mixedExtensions ? 1000 : 0);
        payloads.Rows.Should().Be(folderCount * rowsPerFolder);
        payloads.TotalBytes.Should().BeGreaterThan(40_000_000);
        var beforeRevisit = factory.Client.Requests.Count;
        await DrainFilesAsync(provider, revisit, _ => throw new InvalidOperationException("Completed files were emitted twice."));
        factory.Client.Requests.Should().HaveCount(beforeRevisit);
        long retainedBytes = payloads.CollectAndCountLiveBytes();
        output.WriteLine("Drained {0} rows / {1} response JSON bytes; completed live payload bytes={2}; peak between folders={3}; mixedExtensions={4}.",
            payloads.Rows, payloads.TotalBytes, retainedBytes, peakRetainedBytes, mixedExtensions);
        retainedBytes.Should().Be(0, "completed collections must not accumulate JSON across the Web");
        peakRetainedBytes.Should().Be(0, "consumed payloads must be released between folders, not only after the Web");
        var cached = provider.ReferenceCollector.ReadSurfaceEvidence()
            .Where(surface => surface.ActualMethod == "CACHE" && surface.SurfaceId.StartsWith("files:", StringComparison.Ordinal)).ToArray();
        cached.Should().NotBeEmpty();
        foreach (var surface in cached)
        {
            var original = surface.EvidenceRefs.Single(reference => reference.StartsWith("reused-surface=", StringComparison.Ordinal))[15..];
            var receipts = provider.ReferenceCollector.ReadPaginationEvidence()
                .Where(receipt => receipt.CollectionScopeKey == original).OrderBy(receipt => receipt.PageOrdinal).ToArray();
            AspxPaginationContract.Validate(receipts, DiscoveryTerminalOutcome.Complete).ChainHash
                .Should().Be(surface.PaginationChainHash);
            surface.ExpectedCount.Should().Be(surface.ObservedCount);
        }
        GC.KeepAlive(provider);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Size_backoff_releases_rejected_JSON_while_preserving_attempt_counts_and_digests(bool recover)
    {
        var payloads = new FilePayloadProbe();
        using var factory = new FolderFactory();
        factory.Client.FolderResponse = uri =>
        {
            if (QueryNumber(uri, "$skip", 0) > 0) return FixturePage(uri, Array.Empty<JsonElement>());
            int top = QueryNumber(uri, "$top", 0);
            return payloads.Page(uri, 1, top == 200 && recover ? 7 : top + 1, true);
        };
        using var provider = Provider(factory);
        var root = await ListRootAsync(provider);
        int observed = 0;
        await DrainFilesAsync(provider, root, _ =>
        {
            observed++;
            payloads.CollectAndCountLiveBytes().Should().BeLessThan(100_000,
                "rejected 1001/501-row bodies must not survive the resolved 7-row page");
        });
        observed.Should().Be(recover ? 7 : 0);
        payloads.CollectAndCountLiveBytes().Should().Be(0);
        var surface = provider.ReferenceCollector.ReadSurfaceEvidence().Single(row => row.SurfaceId == "files:" + root.ScopeKey);
        surface.TerminalOutcome.Should().Be(recover ? DiscoveryTerminalOutcome.Complete : DiscoveryTerminalOutcome.Truncated);
        var attempts = surface.EvidenceRefs.Where(reference => reference.StartsWith("file-page-attempt;", StringComparison.Ordinal)).ToArray();
        attempts.Should().Contain(reference => reference.Contains("responseItemCount=1001", StringComparison.Ordinal));
        attempts.Should().Contain(reference => reference.Contains("responseItemCount=501", StringComparison.Ordinal));
        attempts.Should().OnlyContain(reference => reference.Contains("digest=", StringComparison.Ordinal));
        GC.KeepAlive(provider);
    }

    [Fact]
    public async Task One_large_folder_yields_before_reading_next_page_and_releases_consumed_JSON()
    {
        const int rows = 2400;
        const int pageSize = 200;
        int delivered = 0;
        var payloads = new FilePayloadProbe();
        using var factory = new FolderFactory();
        factory.Client.FolderResponse = uri =>
        {
            int skip = QueryNumber(uri, "$skip", 0);
            delivered.Should().Be(skip, "the previous file page must be consumed before another request");
            return skip < rows ? payloads.Page(uri, skip + 1, Math.Min(pageSize, rows - skip), true)
                : FixturePage(uri, Array.Empty<JsonElement>());
        };
        using var provider = Provider(factory);
        var root = await ListRootAsync(provider);
        await using var reader = provider.CreateRawSource(root).ReadBatchesAsync().GetAsyncEnumerator();
        long peakLiveBytes = 0;
        int batches = 0;
        RawDiscoveryBatch last = null;
        while (await reader.MoveNextAsync())
        {
            var batch = reader.Current;
            last = batch;
            delivered += batch.Records.Count;
            batches++;
            factory.Client.Requests.Count(uri => uri.AbsolutePath.EndsWith("/Files", StringComparison.Ordinal))
                .Should().Be(batches, "MoveNext must not prefetch or collect the rest of the folder");
            long live = payloads.CollectAndCountLiveBytes();
            peakLiveBytes = Math.Max(peakLiveBytes, live);
            live.Should().BeLessThanOrEqualTo(2 * payloads.MaximumPageBytes,
                "only the current response and an iterator handoff may retain JSON, never every prior page");
        }
        delivered.Should().Be(rows);
        last.IsTerminal.Should().BeTrue();
        last.TerminalOutcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        payloads.CollectAndCountLiveBytes().Should().Be(0, "even an undiscarded completed enumerator must not retain data pages");
        output.WriteLine("One folder: {0} rows / {1} JSON bytes; largest page={2}; peak live JSON bytes={3}; after EOF=0.",
            rows, payloads.TotalBytes, payloads.MaximumPageBytes, peakLiveBytes);
        GC.KeepAlive(reader);
        GC.KeepAlive(provider);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fallback_releases_both_attempt_payloads_but_retains_files_and_independent_failure_receipts(bool fallbackFails)
    {
        var payloads = new FilePayloadProbe();
        using var factory = new FolderFactory();
        factory.Client.FolderResponse = uri =>
        {
            bool enriched = uri.Query.Contains("ListItemAllFields", StringComparison.Ordinal);
            bool first = QueryNumber(uri, "$skip", 0) == 0;
            return first ? payloads.Page(uri, enriched ? 1 : 101, 200, true)
                : FixturePage(uri, Array.Empty<JsonElement>(),
                    enriched || fallbackFails ? DiscoveryTerminalOutcome.Failed : DiscoveryTerminalOutcome.Complete,
                    enriched || fallbackFails ? "attempt_failed" : null);
        };
        using var provider = Provider(factory);
        var root = await ListRootAsync(provider);
        var identities = new HashSet<string>();
        await DrainFilesAsync(provider, root, record => identities.Add(record.FileUniqueId).Should().BeTrue());
        identities.Should().HaveCount(300, "overlapping observations are deduplicated without dropping fallback-only files");
        payloads.CollectAndCountLiveBytes().Should().Be(0);
        var surfaces = provider.ReferenceCollector.ReadSurfaceEvidence()
            .Where(surface => surface.SurfaceId == "files:" + root.ScopeKey || surface.SurfaceId == "physical-files:" + root.ScopeKey).ToArray();
        surfaces.Should().HaveCount(2);
        surfaces.Single(surface => surface.SurfaceId == "files:" + root.ScopeKey).TerminalOutcome.Should().Be(DiscoveryTerminalOutcome.Failed);
        surfaces.Single(surface => surface.SurfaceId == "physical-files:" + root.ScopeKey).TerminalOutcome
            .Should().Be(fallbackFails ? DiscoveryTerminalOutcome.Failed : DiscoveryTerminalOutcome.Complete);
        provider.ReferenceCollector.ReadPaginationEvidence().Where(receipt =>
            receipt.CollectionScopeKey == "files:" + root.ScopeKey || receipt.CollectionScopeKey == "physical-files:" + root.ScopeKey)
            .Should().HaveCount(4);
        surfaces.Should().OnlyContain(surface => surface.EvidenceRefs.Any(reference => reference.StartsWith("file-page-attempt;", StringComparison.Ordinal)));
        int before = factory.Client.Requests.Count;
        await DrainFilesAsync(provider, root, _ => throw new InvalidOperationException("File emitted twice."));
        if (fallbackFails) factory.Client.Requests.Count.Should().BeGreaterThan(before);
        else factory.Client.Requests.Should().HaveCount(before);
        payloads.CollectAndCountLiveBytes().Should().Be(0);
        GC.KeepAlive(provider);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Early_disposal_or_cancellation_releases_payloads_without_caching_complete_coverage(bool cancel)
    {
        var payloads = new FilePayloadProbe();
        using var cancellation = new CancellationTokenSource();
        using var factory = new FolderFactory();
        factory.Client.FolderResponse = uri => QueryNumber(uri, "$skip", 0) == 0
            ? payloads.Page(uri, 1, 200, true) : FixturePage(uri, Array.Empty<JsonElement>());
        using var provider = Provider(factory);
        var root = await ListRootAsync(provider);
        await ReadThenStopAsync();
        payloads.CollectAndCountLiveBytes().Should().Be(0);
        var incomplete = provider.ReferenceCollector.ReadSurfaceEvidence().Single(surface => surface.SurfaceId == "files:" + root.ScopeKey);
        incomplete.TerminalOutcome.Should().Be(cancel ? DiscoveryTerminalOutcome.Cancelled : DiscoveryTerminalOutcome.Unknown);
        incomplete.ExpectedCount.Should().BeNull();
        incomplete.ContinuationRemaining.Should().BeTrue();
        incomplete.EvidenceRefs.Should().Contain(reference => reference.StartsWith("file-page-attempt;", StringComparison.Ordinal));
        int before = factory.Client.Requests.Count;
        await DrainFilesAsync(provider, root, _ => throw new InvalidOperationException("Previously delivered files must not be duplicated."));
        factory.Client.Requests.Count.Should().BeGreaterThan(before);
        payloads.CollectAndCountLiveBytes().Should().Be(0);
        GC.KeepAlive(provider);

        async Task ReadThenStopAsync()
        {
            await using var reader = provider.CreateRawSource(root).ReadBatchesAsync(cancellation.Token).GetAsyncEnumerator();
            (await reader.MoveNextAsync()).Should().BeTrue();
            reader.Current.Records.Should().HaveCount(200);
            if (cancel)
            {
                cancellation.Cancel();
                var next = async () => await reader.MoveNextAsync();
                await next.Should().ThrowAsync<OperationCanceledException>();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_consumer_persists_all_file_types_before_next_page_and_propagates_storage_errors(bool failStorage)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var storageError = new IOException("offline physical-file commit failed");
        var options = new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection)
            .AddInterceptors(new FileSaveFailure(failStorage ? storageError : null)).Options;
        using var context = new ScanContext(options);
        await context.Database.MigrateAsync();
        using var factory = new FolderFactory();
        factory.Client.FolderResponse = uri =>
        {
            if (!uri.AbsolutePath.EndsWith("/Files", StringComparison.Ordinal) || FolderPath(uri) != RootPath)
                return FixturePage(uri, Array.Empty<JsonElement>());
            int skip = QueryNumber(uri, "$skip", 0);
            if (skip > 0) context.SPFiles.Count().Should().Be(2, "persistence must complete before fetching EOF");
            return FixturePage(uri, skip == 0
                ? new[] { FileRow(1, RootPath + "/document.txt"), FileRow(2, RootPath + "/page.aspx") }
                : Array.Empty<JsonElement>());
        };
        using var provider = Provider(factory);
        var scan = Guid.NewGuid();
        var run = () => new AssessmentWebDiscovery(scan, WebUrl.AbsoluteUri, "/",
            new AssessmentDiscoveryWriter(() => new ScanContext(options))).RunAsync(provider, CancellationToken.None);
        if (failStorage)
        {
            (await run.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(storageError);
            factory.Client.Requests.Count(uri => FolderPath(uri) == RootPath && uri.AbsolutePath.EndsWith("/Files", StringComparison.Ordinal))
                .Should().Be(1);
            (await context.SPFiles.CountAsync()).Should().Be(0);
            provider.ReferenceCollector.ReadSurfaceEvidence().Should().Contain(surface =>
                surface.ActualEndpoint.Contains("/Files", StringComparison.Ordinal) &&
                surface.TerminalOutcome == DiscoveryTerminalOutcome.Unknown);
        }
        else
        {
            await run();
            (await context.SPFiles.Select(file => file.FileName).ToListAsync()).Should().BeEquivalentTo("document.txt", "page.aspx");
            (await context.ClassicPageDiscoveries.Where(row => row.RowType == "Page").ToListAsync())
                .Should().ContainSingle().Which.FileName.Should().Be("page.aspx");
        }
    }

    private static async Task DrainFilesAsync(SharePointLiveAspxDiscoveryProvider provider,
        DiscoveryScopeRegistration scope, Action<RawDiscoveryRecord> consume)
    {
        bool terminal = false;
        await foreach (var batch in provider.CreateRawSource(scope).ReadBatchesAsync())
        {
            foreach (var record in batch.Records) consume(record);
            terminal = batch.IsTerminal;
        }
        terminal.Should().BeTrue();
    }

    private sealed class FileSaveFailure(Exception error) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (error != null && eventData.Context.ChangeTracker.Entries<SPFile>().Any(entry => entry.State == EntityState.Added))
                throw error;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FilePayloadProbe
    {
        private readonly List<(WeakReference Document, int Bytes)> payloads = new();
        internal int Rows { get; private set; }
        internal long TotalBytes => payloads.Sum(payload => (long)payload.Bytes);
        internal int MaximumPageBytes => payloads.Count == 0 ? 0 : payloads.Max(payload => payload.Bytes);

        // The fake transport owns no response payload after returning; JsonElement is what roots its backing document.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal SharePointRestPage Page(Uri endpoint, int first, int count, bool mixed)
        {
            var folder = FolderPath(endpoint);
            var document = JsonSerializer.SerializeToDocument(new
            {
                value = Enumerable.Range(first, count).Select(id => new
                {
                    UniqueId = Guid.Parse("cccccccc-cccc-cccc-cccc-" + id.ToString("D12")),
                    Name = "file-" + id + (mixed && id % 10 == 0 ? ".aspx" : ".txt"),
                    ServerRelativeUrl = folder + "/file-" + id + (mixed && id % 10 == 0 ? ".aspx" : ".txt"),
                    CustomizedPageStatus = "Uncustomized", Length = "4096",
                    Properties = new { vti_x005f_setuppath = "stock.aspx", payload = new string('x', 4096) },
                }),
            });
            payloads.Add((new WeakReference(document), document.RootElement.GetRawText().Length));
            Rows += count;
            return FixturePage(endpoint, document.RootElement.GetProperty("value").EnumerateArray().ToArray());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal long CollectAndCountLiveBytes()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return payloads.Where(payload => payload.Document.IsAlive).Sum(payload => (long)payload.Bytes);
        }
    }
}
