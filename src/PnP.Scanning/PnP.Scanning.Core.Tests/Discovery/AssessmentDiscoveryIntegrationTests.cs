using CsvHelper;
using CsvHelper.Configuration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Globalization;
using System.Runtime.CompilerServices;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class AssessmentDiscoveryIntegrationTests : IClassFixture<ScanContextFixture>
{
    private const string Site = "https://contoso.sharepoint.com/sites/a";
    private readonly ScanContextFixture database;
    public AssessmentDiscoveryIntegrationTests(ScanContextFixture database) => this.database = database;

    [Fact]
    public async Task Denied_list_and_partial_folder_are_scope_rows_and_do_not_discard_pages()
    {
        var scan = Guid.NewGuid();
        using var source = new NativeFixture();
        await Runner(scan).RunAsync(source, CancellationToken.None);
        using var db = database.CreateContext();
        var rows = await db.ClassicPageDiscoveries.Where(row => row.ScanId == scan).ToListAsync();
        rows.Where(row => row.RowType == "Page").Should().HaveCount(2);
        rows.Where(row => row.RowType == "Page").Should().OnlyContain(row => row.DiscoveryStatus == "Discovered");
        var denied = rows.Single(row => row.RecordKey == "scope:denied");
        denied.DiscoveryStatus.Should().Be("Denied");
        denied.FileUniqueId.Should().BeNull();
        denied.ExpectedChildCount.Should().BeNull();
        denied.ErrorCodes.Should().Contain("scope_denied");
        rows.Single(row => row.RecordKey == "scope:partial").DiscoveryStatus.Should().Be("Partial");
        rows.Single(row => row.RecordKey == "scope:good").DiscoveryStatus.Should().Be("Empty");
        rows.Should().Contain(row => row.RecordKey == "scope:missing" && row.DiscoveryStatus == "Unknown");
        rows.Should().NotContain(row => row.RowType == "Page" && row.Url.Contains("restricted"));
    }

    [Fact]
    public async Task Native_report_exports_one_discovery_csv_with_page_and_scope_rows_and_quoted_errors()
    {
        var scan = Guid.NewGuid();
        using var source = new NativeFixture();
        await Runner(scan).RunAsync(source, CancellationToken.None);
        var folder = Path.Combine(Path.GetTempPath(), "assessment-native-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var db = database.CreateContext();
            await ReportManager.ExportClassicReportDataAsync(db, scan, folder,
                new CsvConfiguration(CultureInfo.InvariantCulture));
            Directory.GetFiles(folder, "*gap*.csv").Should().BeEmpty();
            using var csv = new CsvReader(new StreamReader(Path.Combine(folder, "discovery.csv")), CultureInfo.InvariantCulture);
            var rows = csv.GetRecords<ClassicPageDiscovery>().ToArray();
            csv.HeaderRecord.Should().Equal(new[]
            {
                nameof(ClassicPageDiscovery.RecordKey),
                nameof(ClassicPageDiscovery.RowType),
                nameof(ClassicPageDiscovery.ScopeType),
                nameof(ClassicPageDiscovery.ParentScopeKey),
                nameof(ClassicPageDiscovery.Url),
                nameof(ClassicPageDiscovery.SiteCollectionId),
                nameof(ClassicPageDiscovery.WebId),
                nameof(ClassicPageDiscovery.ListId),
                nameof(ClassicPageDiscovery.FolderUniqueId),
                nameof(ClassicPageDiscovery.FileUniqueId),
                nameof(ClassicPageDiscovery.ListItemId),
                nameof(ClassicPageDiscovery.HomePage),
                nameof(ClassicPageDiscovery.LibraryHidden),
                nameof(ClassicPageDiscovery.ObservationMethod),
                nameof(ClassicPageDiscovery.DiscoveryStatus),
                nameof(ClassicPageDiscovery.AssessmentStatus),
                nameof(ClassicPageDiscovery.ExpectedChildCount),
                nameof(ClassicPageDiscovery.ObservedChildCount),
                nameof(ClassicPageDiscovery.ErrorStage),
                nameof(ClassicPageDiscovery.ErrorCodes),
                nameof(ClassicPageDiscovery.ErrorDetail),
                nameof(ClassicPageDiscovery.EvidenceJson),
                nameof(ClassicPageDiscovery.ObservedAtUtc),
                nameof(ClassicPageDiscovery.ScanId),
                nameof(ClassicPageDiscovery.SiteUrl),
                nameof(ClassicPageDiscovery.WebUrl),
            });
            rows.Should().OnlyContain(row => row.ScanId == scan);
            rows.Should().Contain(row => row.RowType == "Page");
            rows.Should().Contain(row => row.RowType == "Scope" && row.DiscoveryStatus == "Denied");
            rows.Single(row => row.RecordKey == "scope:partial").ErrorDetail.Should().Contain("second page, failed\nretry later");
            rows.Where(row => row.RowType == "Page").Should().OnlyContain(row => row.HomePage == null);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task Reobservation_merges_file_identity_without_erasing_enrichment_failure_or_metadata()
    {
        var scan = Guid.NewGuid();
        var writer = Writer();
        var file = Guid.NewGuid();
        var record = File(file, "a.aspx");
        var rich = AssessmentWebDiscovery.Page(scan, Site, "/", new("files", null, DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles, "/pages", "test"), record with { ListItemId = 7, HomePage = true });
        rich.AssessmentStatus = "Failed";
        rich.ErrorStage = "PageMetadata";
        rich.ErrorCodes = "HTTP403";
        await writer.WriteAsync(new[] { rich });
        var sparse = AssessmentWebDiscovery.Page(scan, Site, "/", new("forms", null, DiscoveryScopeKind.Folder,
            DiscoverySourceKind.ListFormBackingFiles, "/forms", "test"), record);
        await writer.WriteAsync(new[] { sparse });
        var rows = await writer.ReadPagesAsync(scan, Site, "/");
        rows.Should().ContainSingle();
        rows[0].ListItemId.Should().Be(7);
        rows[0].HomePage.Should().BeTrue();
        rows[0].DiscoveryStatus.Should().Be("Discovered");
        rows[0].AssessmentStatus.Should().Be("Failed");
        rows[0].ErrorCodes.Should().Contain("HTTP403");
    }

    [Fact]
    public async Task Interrupted_read_preserves_committed_pages_and_pending_scope_for_native_restart()
    {
        var scan = Guid.NewGuid();
        using var cancel = new CancellationTokenSource();
        using var provider = new NativeFixture(cancelAfterFirst: cancel);
        var run = () => Runner(scan).RunAsync(provider, cancel.Token);
        await run.Should().ThrowAsync<OperationCanceledException>();
        (await Writer().ReadPagesAsync(scan, Site, "/")).Should().ContainSingle();
        using var db = database.CreateContext();
        (await db.ClassicPageDiscoveries.SingleAsync(row => row.ScanId == scan && row.RecordKey == "scope:partial"))
            .DiscoveryStatus.Should().Be("Pending");
    }

    [Fact]
    public async Task Repeating_native_web_does_not_duplicate_the_report_inventory()
    {
        var scan = Guid.NewGuid();
        var file = Guid.NewGuid();
        using var first = new NativeFixture(firstFile: file);
        using var second = new NativeFixture(firstFile: file);
        await Runner(scan).RunAsync(first, CancellationToken.None);
        await Runner(scan).RunAsync(second, CancellationToken.None);
        (await Writer().ReadPagesAsync(scan, Site, "/")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Parallel_web_writers_use_the_same_scan_without_sharing_ef_contexts()
    {
        var scan = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async number =>
        {
            var web = "/sub" + number;
            var page = AssessmentWebDiscovery.Page(scan, Site, web,
                new("scope" + number, null, DiscoveryScopeKind.Folder, DiscoverySourceKind.RawListLibraryFiles, web, "test"),
                File(Guid.NewGuid(), "a.aspx") with { WebId = Guid.NewGuid() });
            await Writer().WriteAsync(new[] { page });
        }));
        using var db = database.CreateContext();
        (await db.ClassicPageDiscoveries.CountAsync(row => row.ScanId == scan)).Should().Be(8);
    }

    private AssessmentDiscoveryWriter Writer() => new(database.CreateContext);
    [Fact]
    public void Missing_item_fields_never_collapse_physical_pages_to_their_list_item_number()
    {
        var minimalFields = new Dictionary<string, object> { ["Id"] = 1 };
        var first = Core.Scanners.PageScanComponent.ResolvePhysicalPageUrl(minimalFields, "/Pages/a.aspx");
        var second = Core.Scanners.PageScanComponent.ResolvePhysicalPageUrl(minimalFields, "/OtherPages/b.aspx");
        first.Should().Be("/Pages/a.aspx");
        second.Should().Be("/OtherPages/b.aspx").And.NotBe(first);
        var invalid = () => Core.Scanners.PageScanComponent.ResolvePhysicalPageUrl(minimalFields, "1");
        invalid.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Metadata_for_a_different_file_is_rejected_before_it_enters_classic_page_assessment()
    {
        var fields = new Dictionary<string, object> { ["FileRef"] = "/OtherPages/not-this-file.aspx" };
        var resolve = () => Core.Scanners.PageScanComponent.ResolvePhysicalPageUrl(fields, "/Pages/a.aspx");
        resolve.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("0x010100C568DB52D9D0A14D9B2FDCC96666E9F2007948130EC3DB064584E219954237AF39004C1F8B46085B4D22B1CDC3DE08CFFB9C0", true)]
    [InlineData("0x0101009D1CB255DA76424F860D91F20E6C4118", false)]
    [InlineData(null, false)]
    public void Publishing_page_classification_recognizes_actual_enterprise_wiki_content_type(string id, bool expected)
    {
        SharePointLiveAspxDiscoveryProvider.IsPublishingPageContentType(id).Should().Be(expected);
    }

    [Fact]
    public async Task Conflicting_metadata_keeps_one_physical_page_and_a_visible_discovery_error()
    {
        var scan = Guid.NewGuid();
        var scope = new DiscoveryScopeRegistration("files", null, DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles, "/pages", "test");
        var file = File(Guid.NewGuid(), "a.aspx");
        var first = AssessmentWebDiscovery.Page(scan, Site, "/", scope, file with { HomePage = true });
        var second = AssessmentWebDiscovery.Page(scan, Site, "/", scope, file with { HomePage = false });
        await Writer().WriteAsync(new[] { first });
        await Writer().WriteAsync(new[] { second });
        var rows = await Writer().ReadPagesAsync(scan, Site, "/");
        rows.Should().ContainSingle();
        rows[0].DiscoveryStatus.Should().Be("Discovered");
        rows[0].ErrorCodes.Should().Contain(DiscoveryGapCodes.ChangedDuringScan);
    }

    [Fact]
    public async Task Web_coverage_remains_partial_when_any_scope_was_denied()
    {
        var scan = Guid.NewGuid();
        using var provider = new NativeFixture();
        await Runner(scan).RunAsync(provider, CancellationToken.None);
        (await Writer().ReadWebCoverageAsync(scan, Site, "/")).Should().Be("Partial");
    }

    [Fact]
    public async Task Old_classic_report_accepts_unknown_homepage_and_new_identity_fields()
    {
        var scan = Guid.NewGuid();
        var file = Guid.NewGuid();
        using (var db = database.CreateContext())
        {
            db.ClassicPages.Add(new ClassicPage
            {
                ScanId = scan, SiteUrl = Site, WebUrl = "/", PageUrl = "/Pages/a.aspx", PageType = "WikiPage",
                HomePage = null, FileUniqueId = file, ListItemId = 4, DiscoveryStatus = "Discovered", AssessmentStatus = "Failed",
            });
            await db.SaveChangesAsync();
        }
        using var read = database.CreateContext();
        var page = await read.ClassicPages.SingleAsync(row => row.ScanId == scan);
        page.HomePage.Should().BeNull();
        page.FileUniqueId.Should().Be(file);
        page.AssessmentStatus.Should().Be("Failed");
    }

    [Fact]
    public void Direct_list_item_guid_classifies_modern_page_without_string_cast_failure()
    {
        var fields = new Dictionary<string, object>
        {
            ["ClientSideApplicationId"] = Guid.Parse("B6917CB1-93A0-4B97-A84D-7CF49975D4EC"),
        };
        Core.Scanners.PageScanComponent.GetPageType(fields).Should().Be("ModernPage");
    }

    [Fact]
    public async Task Persistence_failure_is_not_converted_to_an_empty_discovery_scope()
    {
        var scan = Guid.NewGuid();
        using var provider = new NativeFixture();
        var writer = new AssessmentDiscoveryWriter(() => throw new IOException("storage unavailable"));
        var run = () => new AssessmentWebDiscovery(scan, Site, "/", writer).RunAsync(provider, CancellationToken.None);
        await run.Should().ThrowAsync<IOException>().WithMessage("storage unavailable");
    }

    private AssessmentWebDiscovery Runner(Guid scan) => new(scan, Site, "/", Writer());
    private static RawDiscoveryRecord File(Guid id, string name) => new(id.ToString(), id.ToString(), "list", name,
        "/sites/a/pages/" + name, true, "test", SiteCollectionId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        WebId: Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private sealed class NativeFixture(Guid? firstFile = null, CancellationTokenSource cancelAfterFirst = null) : IAspxDiscoveryProvider
    {
        private readonly Guid fileId = firstFile ?? Guid.NewGuid();
        public DiscoveryScopeRegistration RootScope { get; } = new("web", null, DiscoveryScopeKind.Web, null, Site, "test");
        public Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(DiscoveryScopeRegistration parent,
            CancellationToken cancellationToken = default)
        {
            if (parent.ScopeKey == "web")
            {
                var children = new[]
                {
                    new DiscoveryScopeRegistration("partial", "web", DiscoveryScopeKind.Container, DiscoverySourceKind.RawListLibraryFiles, "/pages", "test"),
                    new DiscoveryScopeRegistration("denied", "web", DiscoveryScopeKind.Container, null, "/restricted", "test"),
                    new DiscoveryScopeRegistration("good", "web", DiscoveryScopeKind.Container, null, "/empty", "test"),
                };
                var expected = children.Select(row => new DiscoveryChildExpectation(row.ScopeKey, row.Kind, row.SourceKind, row.Locator, "test"))
                    .Append(new DiscoveryChildExpectation("missing", DiscoveryScopeKind.Container, null, "/missing", "test")).ToArray();
                return Task.FromResult(new DiscoveryChildEnumerationResult(DiscoveryScopeKind.Container, expected, children,
                    DiscoveryTerminalOutcome.Complete, "test"));
            }
            return Task.FromResult(new DiscoveryChildEnumerationResult(DiscoveryScopeKind.Folder, Array.Empty<DiscoveryChildExpectation>(),
                Array.Empty<DiscoveryScopeRegistration>(), parent.ScopeKey == "denied" ? DiscoveryTerminalOutcome.Denied : DiscoveryTerminalOutcome.Empty, "test"));
        }
        public IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface) => new Source(fileId, cancelAfterFirst);
        public void Dispose() { }
    }

    private sealed class Source(Guid fileId, CancellationTokenSource cancel) : IRawDiscoverySource
    {
        public async IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new(0, "request-1", "response-1", new[] { File(fileId, "a.aspx") }, false, DiscoveryTerminalOutcome.Pending, "page2");
            await Task.Yield();
            cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(1, "request-2", "response-2", new[] { File(Guid.Parse("33333333-3333-3333-3333-333333333333"), "b.aspx") },
                true, DiscoveryTerminalOutcome.Truncated, GapCode: "page2_failed", GapDetail: "second page, failed\nretry later");
        }
    }
}
