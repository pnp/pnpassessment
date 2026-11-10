using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Compatibility;
using PnP.Scanning.Core.ClassicPages.Collection.Discovery;
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
public sealed class AssessmentDiscoveryIntegrationTests : IDisposable
{
    private const string Site = "https://contoso.sharepoint.com/sites/a";
    private readonly ScanContextFixture database = new();

    /// <summary>Disposes the test-owned scan database; separate assessments do not share one physical-file table in production.</summary>
    public void Dispose() => database.Dispose();

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

    /// <summary>Verifies native report exports one discovery csv with page and scope rows and quoted errors.</summary>
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
            await ReportManager.ExportClassicReportDataAsync(
                db,
                scan,
                folder,
                new CsvConfiguration(CultureInfo.InvariantCulture));
            Directory.GetFiles(folder, "*gap*.csv").Should().BeEmpty();
            using CsvReader csv = new CsvReader(
                new StreamReader(Path.Combine(folder, "discovery.csv")),
                CultureInfo.InvariantCulture);
            var rows = csv.GetRecords<ClassicPageDiscovery>().ToArray();
            csv.HeaderRecord.Should()
                .Equal(
                    new[]
                    {
                        nameof(ClassicPageDiscovery.RecordKey), nameof(ClassicPageDiscovery.RowType),
                        nameof(ClassicPageDiscovery.ScopeType), nameof(ClassicPageDiscovery.ParentScopeKey),
                        nameof(ClassicPageDiscovery.Url), nameof(ClassicPageDiscovery.SiteCollectionId),
                        nameof(ClassicPageDiscovery.WebId), nameof(ClassicPageDiscovery.ListId),
                        nameof(ClassicPageDiscovery.FolderUniqueId), nameof(ClassicPageDiscovery.FileUniqueId),
                        nameof(ClassicPageDiscovery.ListItemId), nameof(ClassicPageDiscovery.FileName),
                        nameof(ClassicPageDiscovery.CustomizedPageStatus), nameof(ClassicPageDiscovery.SetupPath),
                        nameof(ClassicPageDiscovery.HomePage), nameof(ClassicPageDiscovery.LibraryHidden),
                        nameof(ClassicPageDiscovery.ObservationMethod),
                        nameof(ClassicPageDiscovery.DiscoveryStatus), nameof(ClassicPageDiscovery.AssessmentStatus),
                        nameof(ClassicPageDiscovery.ExpectedChildCount),
                        nameof(ClassicPageDiscovery.ObservedChildCount), nameof(ClassicPageDiscovery.ErrorStage),
                        nameof(ClassicPageDiscovery.ErrorCodes), nameof(ClassicPageDiscovery.ErrorDetail),
                        nameof(ClassicPageDiscovery.EvidenceJson), nameof(ClassicPageDiscovery.ObservedAtUtc),
                        nameof(ClassicPageDiscovery.ScanId), nameof(ClassicPageDiscovery.SiteUrl),
                        nameof(ClassicPageDiscovery.WebUrl),
                    });
            rows.Should().OnlyContain(row => row.ScanId == scan);
            rows.Should().Contain(row => row.RowType == "Page");
            rows.Should().Contain(row => row.RowType == "Scope" && row.DiscoveryStatus == "Denied");
            rows.Single(row => row.RecordKey == "scope:partial")
                .ErrorDetail.Should()
                .Contain("second page, failed\nretry later");
            rows.Where(row => row.RowType == "Page").Should().OnlyContain(row => row.HomePage == null);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Verifies reobservation merges file identity without erasing enrichment failure or metadata.</summary>
    [Fact]
    public async Task Reobservation_merges_file_identity_without_erasing_enrichment_failure_or_metadata()
    {
        var scan = Guid.NewGuid();
        var writer = Writer();
        var file = Guid.NewGuid();
        var record = File(file, "a.aspx");
        DiscoveryEvidence rich = AssessmentWebDiscovery.Page(
            scan,
            Site,
            "/",
            new("files", null, DiscoveryScopeKind.Folder, DiscoverySourceKind.RawListLibraryFiles, "/pages", "test"),
            record with { ListItemId = 7, HomePage = true, CustomizedPageStatusRaw = "1" });
        rich.AssessmentStatus = "Failed";
        rich.ErrorStage = "PageMetadata";
        rich.ErrorCodes = "HTTP403";
        await writer.WriteAsync(new[] { rich });
        DiscoveryEvidence sparse = AssessmentWebDiscovery.Page(
            scan,
            Site,
            "/",
            new("forms", null, DiscoveryScopeKind.Folder, DiscoverySourceKind.ListFormBackingFiles, "/forms", "test"),
            record);
        await writer.WriteAsync(new[] { sparse });
        var rows = await writer.ReadPagesAsync(scan, Site, "/");
        rows.Should().ContainSingle();
        rows[0].ListItemId.Should().Be(7);
        rows[0].HomePage.Should().BeTrue();
        rows[0].CustomizedPageStatus.Should().Be("Uncustomized");
        rows[0].DiscoveryStatus.Should().Be("Discovered");
        rows[0].AssessmentStatus.Should().Be("Failed");
        rows[0].ErrorCodes.Should().Contain("HTTP403");
    }

    /// <summary>Verifies changed customization status is preserved with a visible conflict.</summary>
    [Fact]
    public async Task Changed_customization_status_is_preserved_with_a_visible_conflict()
    {
        var scan = Guid.NewGuid();
        DiscoveryScopeRegistration scope = new DiscoveryScopeRegistration(
            "files",
            null,
            DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles,
            "/pages",
            "test");
        var file = File(Guid.NewGuid(), "a.aspx");
        var first = AssessmentWebDiscovery.Page(scan, Site, "/", scope, file with { CustomizedPageStatusRaw = "1" });
        var second = AssessmentWebDiscovery.Page(scan, Site, "/", scope, file with { CustomizedPageStatusRaw = "2" });
        await Writer().WriteAsync(new[] { first });
        await Writer().WriteAsync(new[] { second });
        var row = (await Writer().ReadPagesAsync(scan, Site, "/")).Single();
        row.CustomizedPageStatus.Should().Be("Customized");
        row.ErrorCodes.Should().Contain(DiscoveryGapCodes.ChangedDuringScan);
        row.ErrorDetail.Should().Contain("CustomizedPageStatus");
    }

    /// <summary>Verifies page customization status is normalized and projected.</summary>
    [Theory]
    [InlineData("0", "None")]
    [InlineData("1", "Uncustomized")]
    [InlineData("2", "Customized")]
    [InlineData("None", "None")]
    [InlineData(" uncustomized ", "Uncustomized")]
    [InlineData("Customized", "Customized")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void Page_customization_status_is_normalized_and_projected(string raw, string expected)
    {
        DiscoveryScopeRegistration scope = new DiscoveryScopeRegistration(
            "files",
            null,
            DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles,
            "/pages",
            "test");
        DiscoveryEvidence row = AssessmentWebDiscovery.Page(
            Guid.NewGuid(),
            Site,
            "/",
            scope,
            File(Guid.NewGuid(), "a.aspx") with { CustomizedPageStatusRaw = raw });
        row.CustomizedPageStatus.Should().Be(expected);
        row.ErrorCodes.Should().BeNull();
        ClassicPageRow page = FileLevelPageProjection.Project(
            row,
            FileLevelPageProjection.ListAssociationState.Resolved);
        page.CustomizedPageStatus.Should().Be(expected);
    }

    /// <summary>Verifies invalid customization status retains page and records error.</summary>
    [Theory]
    [InlineData("99")]
    [InlineData("unknown")]
    public void Invalid_customization_status_retains_page_and_records_error(string raw)
    {
        DiscoveryScopeRegistration scope = new DiscoveryScopeRegistration(
            "files",
            null,
            DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles,
            "/pages",
            "test");
        DiscoveryEvidence row = AssessmentWebDiscovery.Page(
            Guid.NewGuid(),
            Site,
            "/",
            scope,
            File(Guid.NewGuid(), "a.aspx") with { CustomizedPageStatusRaw = raw });
        row.CustomizedPageStatus.Should().BeNull();
        row.DiscoveryStatus.Should().Be("Discovered");
        row.ErrorCodes.Should().Contain("InvalidCustomizedPageStatus");
    }

    /// <summary>Verifies interrupted read preserves committed pages and pending scope for native restart.</summary>
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
            .DiscoveryStatus.Should()
            .Be("Pending");
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

    /// <summary>Verifies parallel web writers use the same scan without sharing ef contexts.</summary>
    [Fact]
    public async Task Parallel_web_writers_use_the_same_scan_without_sharing_ef_contexts()
    {
        var scan = Guid.NewGuid();
        await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(async number =>
                {
                    string web = "/sub" + number;
                    DiscoveryEvidence page = AssessmentWebDiscovery.Page(
                        scan,
                        Site,
                        web,
                        new(
                            "scope" + number,
                            null,
                            DiscoveryScopeKind.Folder,
                            DiscoverySourceKind.RawListLibraryFiles,
                            web,
                            "test"),
                        File(Guid.NewGuid(), "a.aspx") with { WebId = Guid.NewGuid() });
                    await Writer().WriteAsync(new[] { page });
                }));
        using var db = database.CreateContext();
        (await db.ClassicPageDiscoveries.CountAsync(row => row.ScanId == scan)).Should().Be(8);
    }

    private AssessmentDiscoveryWriter Writer() => new(database.CreateContext);

    /// <summary>Verifies missing item fields never collapse physical pages to their list item number.</summary>
    [Fact]
    public void Missing_item_fields_never_collapse_physical_pages_to_their_list_item_number()
    {
        var minimalFields = new Dictionary<string, object> { ["Id"] = 1 };
        string first = ClassicPageRules.ResolvePhysicalPageUrl(minimalFields, "/Pages/a.aspx");
        string second = ClassicPageRules.ResolvePhysicalPageUrl(minimalFields, "/OtherPages/b.aspx");
        first.Should().Be("/Pages/a.aspx");
        second.Should().Be("/OtherPages/b.aspx").And.NotBe(first);
        Func<string> invalid = () => ClassicPageRules.ResolvePhysicalPageUrl(minimalFields, "1");
        invalid.Should().Throw<InvalidDataException>();
    }

    /// <summary>Verifies metadata for a different file is rejected before it enters classic page assessment.</summary>
    [Fact]
    public void Metadata_for_a_different_file_is_rejected_before_it_enters_classic_page_assessment()
    {
        var fields = new Dictionary<string, object> { ["FileRef"] = "/OtherPages/not-this-file.aspx" };
        Func<string> resolve = () => ClassicPageRules.ResolvePhysicalPageUrl(fields, "/Pages/a.aspx");
        resolve.Should().Throw<InvalidDataException>();
    }

    /// <summary>Verifies publishing page classification recognizes actual enterprise wiki content type.</summary>
    [Theory]
    [InlineData(
        "0x010100C568DB52D9D0A14D9B2FDCC96666E9F2007948130EC3DB064584E219954237AF39004C1F8B46085B4D22B1CDC3DE08CFFB9C0",
        true)]
    [InlineData("0x0101009D1CB255DA76424F860D91F20E6C4118", false)]
    [InlineData(null, false)]
    public void Publishing_page_classification_recognizes_actual_enterprise_wiki_content_type(string id, bool expected)
    {
        ClassicPageRules.IsPublishingContentType(id).Should().Be(expected);
    }

    /// <summary>Verifies conflicting metadata keeps one physical page and a visible discovery error.</summary>
    [Fact]
    public async Task Conflicting_metadata_keeps_one_physical_page_and_a_visible_discovery_error()
    {
        var scan = Guid.NewGuid();
        DiscoveryScopeRegistration scope = new DiscoveryScopeRegistration(
            "files",
            null,
            DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles,
            "/pages",
            "test");
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

    /// <summary>Verifies old classic report accepts unknown homepage and new identity fields.</summary>
    [Fact]
    public async Task Old_classic_report_accepts_unknown_homepage_and_new_identity_fields()
    {
        var scan = Guid.NewGuid();
        var file = Guid.NewGuid();
        using (var db = database.CreateContext())
        {
            db.ClassicPages.Add(
                new ClassicPage
                {
                    ScanId = scan,
                    SiteUrl = Site,
                    WebUrl = "/",
                    PageUrl = "/Pages/a.aspx",
                    PageType = "WikiPage",
                    HomePage = null,
                    FileUniqueId = file,
                    ListItemId = 4,
                    DiscoveryStatus = "Discovered",
                    AssessmentStatus = "Failed",
                });
            await db.SaveChangesAsync();
        }

        using var read = database.CreateContext();
        var page = await read.ClassicPages.SingleAsync(row => row.ScanId == scan);
        page.HomePage.Should().BeNull();
        page.FileUniqueId.Should().Be(file);
        page.AssessmentStatus.Should().Be("Failed");
    }

    /// <summary>Verifies direct list item guid classifies modern page without string cast failure.</summary>
    [Fact]
    public void Direct_list_item_guid_classifies_modern_page_without_string_cast_failure()
    {
        var fields = new Dictionary<string, object>
        {
            ["ClientSideApplicationId"] = Guid.Parse("B6917CB1-93A0-4B97-A84D-7CF49975D4EC"),
        };
        ClassicPageRules.GetPageType(fields).Should().Be("ModernPage");
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

    /// <summary>Verifies file.</summary>
    private static RawDiscoveryRecord File(Guid id, string name) =>
        new(
            id.ToString(),
            id.ToString(),
            "list",
            name,
            "/sites/a/pages/" + name,
            true,
            "test",
            SiteCollectionId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            WebId: Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private sealed class NativeFixture(Guid? firstFile = null, CancellationTokenSource cancelAfterFirst = null)
        : IAspxDiscoveryProvider
    {
        private readonly Guid fileId = firstFile ?? Guid.NewGuid();

        public DiscoveryScopeRegistration RootScope { get; } = new(
            "web",
            null,
            DiscoveryScopeKind.Web,
            null,
            Site,
            "test");

        /// <summary>Dispatches child-scope discovery by the registered scope kind.</summary>
        public Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(
            DiscoveryScopeRegistration parent,
            CancellationToken cancellationToken = default)
        {
            if (parent.ScopeKey == "web")
            {
                var children = new[]
                {
                    new DiscoveryScopeRegistration(
                        "partial",
                        "web",
                        DiscoveryScopeKind.Container,
                        DiscoverySourceKind.RawListLibraryFiles,
                        "/pages",
                        "test"),
                    new DiscoveryScopeRegistration(
                        "denied",
                        "web",
                        DiscoveryScopeKind.Container,
                        null,
                        "/restricted",
                        "test"),
                    new DiscoveryScopeRegistration(
                        "good",
                        "web",
                        DiscoveryScopeKind.Container,
                        null,
                        "/empty",
                        "test"),
                };
                DiscoveryChildExpectation[] expected = children
                    .Select(row => new DiscoveryChildExpectation(
                        row.ScopeKey,
                        row.Kind,
                        row.SourceKind,
                        row.Locator,
                        "test"))
                    .Append(
                        new DiscoveryChildExpectation(
                            "missing",
                            DiscoveryScopeKind.Container,
                            null,
                            "/missing",
                            "test"))
                    .ToArray();
                return Task.FromResult(
                    new DiscoveryChildEnumerationResult(
                        DiscoveryScopeKind.Container,
                        expected,
                        children,
                        DiscoveryTerminalOutcome.Complete,
                        "test"));
            }

            return Task.FromResult(
                new DiscoveryChildEnumerationResult(
                    DiscoveryScopeKind.Folder,
                    Array.Empty<DiscoveryChildExpectation>(),
                    Array.Empty<DiscoveryScopeRegistration>(),
                    parent.ScopeKey == "denied" ? DiscoveryTerminalOutcome.Denied : DiscoveryTerminalOutcome.Empty,
                    "test"));
        }

        /// <summary>Verifies create raw source.</summary>
        public IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface) =>
            new Source(fileId, cancelAfterFirst);

        /// <summary>Verifies dispose.</summary>
        public void Dispose()
        {
        }
    }

    private sealed class Source(Guid fileId, CancellationTokenSource cancel) : IRawDiscoverySource
    {
        /// <summary>Verifies read batches async.</summary>
        public async IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new(
                0,
                "request-1",
                "response-1",
                new[] { File(fileId, "a.aspx") },
                false,
                DiscoveryTerminalOutcome.Pending,
                "page2");
            await Task.Yield();
            cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(
                1,
                "request-2",
                "response-2",
                new[] { File(Guid.Parse("33333333-3333-3333-3333-333333333333"), "b.aspx") },
                true,
                DiscoveryTerminalOutcome.Truncated,
                GapCode: "page2_failed",
                GapDetail: "second page, failed\nretry later");
        }
    }
}