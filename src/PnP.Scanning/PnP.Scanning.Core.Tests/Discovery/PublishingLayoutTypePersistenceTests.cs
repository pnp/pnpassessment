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
using System.Text.Json;
using Xunit;
using static PnP.Scanning.Core.Tests.Discovery.PublishingLayoutTypeEvidenceTests;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed class PublishingLayoutTypePersistenceTests : IClassFixture<ScanContextFixture>
{
    private const string Site = "https://contoso.sharepoint.com/sites/types";
    private readonly ScanContextFixture database;
    public PublishingLayoutTypePersistenceTests(ScanContextFixture database) => this.database = database;
    private AssessmentDiscoveryWriter Writer() => new(database.CreateContext);

    [Fact]
    public async Task Production_batch_seam_reads_own_source_without_items_and_preserves_failed_files_in_native_csv()
    {
        var scan = Guid.NewGuid();
        using var metadata = new MetadataFixture();
        var catalog = PublishingLayoutTypeCatalog.Capture(new[] { metadata.Path });
        using var provider = new FileProvider();
        var acquired = new List<string>();
        var authority = new Core.Storage.Scan
        {
            ScanId = scan, PublishingLayoutRuleVersion = 1, PublishingLayoutTypeCatalogJson = catalog.ToJson(),
        };
        await new AssessmentWebDiscovery(scan, Site, "/", Writer(),
            PublishingLayoutTypeEvidence.ForScan(authority, async (physical, _) =>
            {
                (await Writer().ReadPagesAsync(scan, Site, "/")).Should().Contain(value => value.RecordKey == physical.RecordKey,
                    "existence is persisted before downloading source");
                acquired.Add(physical.Url);
                return physical.FileName switch
                {
                    "Denied.aspx" => throw new UnauthorizedAccessException("Denied"),
                    "Failed.aspx" => throw new IOException("Source failed"),
                    "Missing.aspx" => null,
                    _ => Source(Identity("Indirect")),
                };
            })).RunAsync(provider, default);
        acquired.Should().BeEquivalentTo(provider.Files.Select(value => value.PhysicalLocator));
        var rows = await Writer().ReadPagesAsync(scan, Site, "/");
        rows.Should().HaveCount(4).And.OnlyContain(row => row.ListItemId == null && row.DiscoveryStatus == "Discovered");
        rows.Single(row => row.FileName == "Indirect.aspx").PublishingLayoutFamily.Should().Be("Member");
        foreach (var status in new[] { "Denied", "Failed", "Unknown" })
        {
            var row = rows.Single(row => row.PageTypeSourceStatus == status);
            row.PublishingLayoutFamily.Should().Be("Unknown");
            row.PageTypeReason.Should().NotBe("NotEvaluated");
        }
        using var db = database.CreateContext();
        var original = await db.ClassicPageDiscoveries.Where(row => row.ScanId == scan).ToListAsync();
        original.Should().Contain(row => row.RowType == "Scope" && row.DiscoveryStatus == "Complete");
        var directory = Path.Combine(Path.GetTempPath(), "type-report-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            await ReportManager.ExportClassicReportDataAsync(db, scan, directory, new CsvConfiguration(CultureInfo.InvariantCulture));
            using var csv = new CsvReader(new StreamReader(Path.Combine(directory, "discovery.csv")), CultureInfo.InvariantCulture);
            csv.Context.TypeConverterOptionsCache.GetOptions<string>().NullValues.Add("");
            var exported = csv.GetRecords<ClassicPageDiscovery>().ToArray();
            // Native CSV uses empty fields for null strings and second-precision timestamps.
            exported.Should().BeEquivalentTo(original, config => config.Excluding(row => row.PageType)
                .Using<string>(ctx => (ctx.Subject ?? "").Should().Be(ctx.Expectation ?? "")).WhenTypeIs<string>()
                .Using<DateTime>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, TimeSpan.FromSeconds(1))).WhenTypeIs<DateTime>());
            exported.Single(row => row.FileName == "Indirect.aspx").ContentTypeId.Should().Be(AspxAssetPurpose.LayoutContentType);
            exported.Where(row => row.RowType == "Page").Should().OnlyContain(row => row.PageTypeEvidenceOrigin == "DeclaredSource");
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("Denied")]
    [InlineData("Failed")]
    [InlineData("Unknown")]
    public async Task Reobservation_and_metadata_updates_retain_failures_and_all_type_observations(string status)
    {
        var scan = Guid.NewGuid();
        var first = Page(scan);
        first.FileUniqueId = Guid.NewGuid();
        first.SiteCollectionId = Guid.NewGuid();
        first.WebId = Guid.NewGuid();
        first.FileName = "file.aspx";
        first.DiscoveryStatus = status;
        first.AssessmentStatus = status;
        first.ErrorDetail = "Original evidence";
        await PublishingLayoutTypeEvidence.AcquireAsync(first, (_, _) => status switch
        {
            "Denied" => throw new UnauthorizedAccessException(),
            "Failed" => throw new IOException(),
            _ => Task.FromResult<string>(null),
        }, new(), default);
        await Writer().WriteAsync(new[] { first });
        var later = Page(scan);
        await PublishingLayoutTypeEvidence.AcquireAsync(later, (_, _) => Task.FromResult(Source(Root)), new(), default);
        await Writer().WriteAsync(new[] { later });
        var metadataOnly = Page(scan);
        metadataOnly.Url = null;
        metadataOnly.AssessmentStatus = "Complete";
        await Writer().UpdateExistingAsync(new[] { metadataOnly });
        var row = (await Writer().ReadPagesAsync(scan, Site, "/")).Single();
        row.DiscoveryStatus.Should().Be(status);
        row.AssessmentStatus.Should().Be(status);
        row.PageTypeSourceStatus.Should().Be(status);
        row.PageTypeResolutionStatus.Should().Be("Unknown");
        row.PublishingLayoutFamily.Should().Be("Unknown");
        row.PageTypeReason.Should().Contain(status == "Unknown" ? "SourceUnavailable" : "Source" + status);
        row.ErrorDetail.Should().Be("Original evidence");
        row.FileUniqueId.Should().Be(first.FileUniqueId);
        row.SiteCollectionId.Should().Be(first.SiteCollectionId);
        row.WebId.Should().Be(first.WebId);
        row.FileName.Should().Be(first.FileName);
        row.Url.Should().Be(first.Url);
        JsonSerializer.Deserialize<PublishingLayoutTypeEvidence.Observation[]>(row.PageTypeEvidenceJson).Should().HaveCount(2);
    }

    [Fact]
    public async Task Conflicting_declarations_cannot_keep_a_confirmed_decision()
    {
        var scan = Guid.NewGuid();
        foreach (var identity in new[] { Root, PublishingLayoutTypeCatalog.ObjectIdentity })
        {
            var row = Page(scan);
            await PublishingLayoutTypeEvidence.AcquireAsync(row, (_, _) => Task.FromResult(Source(identity)), new(), default);
            await Writer().WriteAsync(new[] { row });
        }
        var retained = (await Writer().ReadPagesAsync(scan, Site, "/")).Single();
        retained.PublishingLayoutFamily.Should().Be("Unknown");
        retained.PageTypeReason.Should().Contain("ConflictingTypeObservations");
        retained.DeclaredPageType.Should().Contain(Root).And.Contain(PublishingLayoutTypeCatalog.ObjectIdentity);
        retained.ResolvedPageType.Should().BeNull();
        PublishingLayoutTypeEvidence.IsConfirmedMember(retained).Should().BeFalse();
    }

    [Theory]
    [InlineData("Denied")]
    [InlineData("Failed")]
    [InlineData("Unknown")]
    public async Task Coverage_failure_and_unknown_counts_do_not_become_success_or_zero(string status)
    {
        var scan = Guid.NewGuid();
        await Writer().WriteAsync(new[] { new ClassicPageDiscovery
        {
            ScanId = scan, RecordKey = "scope:files", RowType = "Scope", ScopeType = "Folder",
            DiscoveryStatus = status, ErrorCodes = "original", ObservedChildCount = null, ExpectedChildCount = null,
        } });
        await Writer().WriteAsync(new[] { new ClassicPageDiscovery
        {
            ScanId = scan, RecordKey = "scope:files", RowType = "Scope", ScopeType = "Folder",
            DiscoveryStatus = "Empty", ObservedChildCount = 0, ExpectedChildCount = 0,
        } });
        using var db = database.CreateContext();
        var row = await db.ClassicPageDiscoveries.SingleAsync(value => value.ScanId == scan);
        row.DiscoveryStatus.Should().Be(status);
        row.ObservedChildCount.Should().BeNull();
        row.ExpectedChildCount.Should().BeNull();
        row.ErrorCodes.Should().Be("original");
    }

    private static ClassicPageDiscovery Page(Guid scan) => new()
    {
        ScanId = scan, RecordKey = "page:physical", RowType = "Page", DiscoveryStatus = "Discovered",
        SiteUrl = Site, WebUrl = "/", Url = "/outside/file.aspx", ContentTypeId = AspxAssetPurpose.PublishingContentType,
    };

    private sealed class FileProvider : IAspxDiscoveryProvider
    {
        public DiscoveryScopeRegistration RootScope { get; } = new("web", null, DiscoveryScopeKind.Web, null, Site, "test");
        private readonly DiscoveryScopeRegistration folder = new("files", "web", DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles, "/outside", "test");
        internal RawDiscoveryRecord[] Files { get; } = new[] { "Indirect", "Denied", "Failed", "Missing" }.Select(name =>
            new RawDiscoveryRecord(name, Guid.NewGuid().ToString(), "files", name + ".aspx", "/outside/" + name + ".aspx",
                true, "test", SiteCollectionId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                WebId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
                ContentTypeId: AspxAssetPurpose.LayoutContentType)).ToArray();

        public Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(DiscoveryScopeRegistration parent, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiscoveryChildEnumerationResult(DiscoveryScopeKind.Folder, Array.Empty<DiscoveryChildExpectation>(),
                parent == RootScope ? new[] { folder } : Array.Empty<DiscoveryScopeRegistration>(), DiscoveryTerminalOutcome.Complete, "test"));
        public IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface) => new SourceBatch(Files);
        public void Dispose() { }
    }

    private sealed class SourceBatch(RawDiscoveryRecord[] files) : IRawDiscoverySource
    {
        public async IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new(1, null, null, files, true, DiscoveryTerminalOutcome.Complete);
        }
    }
}
