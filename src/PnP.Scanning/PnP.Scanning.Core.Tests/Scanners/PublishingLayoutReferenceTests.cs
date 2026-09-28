using CsvHelper;
using CsvHelper.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.SharePoint.Client;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Discovery;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Globalization;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners;

public sealed class PublishingLayoutReferenceTests : IClassFixture<ScanContextFixture>
{
    private const string Site = "https://contoso.sharepoint.com/sites/layouts";
    private const string LayoutUrl = "/sites/layouts/_catalogs/masterpage/Article%20Left.aspx";
    private static readonly Guid SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly ScanContextFixture database;

    public PublishingLayoutReferenceTests(ScanContextFixture database) => this.database = database;

    [Theory]
    [InlineData("https://CONTOSO.sharepoint.com/sites/layouts/_catalogs/masterpage/Article%20Left.aspx", LayoutUrl)]
    [InlineData(" /sites/layouts/_catalogs/masterpage/Article Left.aspx ", LayoutUrl)]
    [InlineData("/sites/layouts/_catalogs/./masterpage/Article%20Left.aspx", LayoutUrl)]
    [InlineData("/sites/layouts/_catalogs/masterpage/a%2520b.aspx", "/sites/layouts/_catalogs/masterpage/a%2520b.aspx")]
    [InlineData("/sites/layouts/_catalogs/masterpage/a%23b.aspx", "/sites/layouts/_catalogs/masterpage/a%23b.aspx")]
    [InlineData("https://other.example/sites/layouts/_catalogs/masterpage/Article%20Left.aspx", null)]
    [InlineData("http://contoso.sharepoint.com/sites/layouts/layout.aspx", null)]
    [InlineData("ArticleLeft", null)]
    [InlineData("_catalogs/masterpage/layout.aspx", null)]
    [InlineData("//other.example/layout.aspx", null)]
    [InlineData("/sites/layouts/layout.aspx?x=1", null)]
    [InlineData("/sites/layouts/layout.aspx#part", null)]
    [InlineData("/sites/layouts/a%2fb.aspx", null)]
    [InlineData("/sites/layouts/a%5Cb.aspx", null)]
    [InlineData("/sites/layouts/a%zz.aspx", null)]
    [InlineData("/sites/layouts/a%.aspx", null)]
    [InlineData("/sites/layouts/a%00.aspx", null)]
    [InlineData("/sites/layouts/a\\b.aspx", null)]
    [InlineData("/sites/layouts//layout.aspx", null)]
    [InlineData(null, null)]
    public void Normalizes_only_unambiguous_same_origin_file_urls(string value, string expected)
        => PublishingLayoutReference.NormalizeUrl(value, Site).Should().Be(expected);

    [Fact]
    public void Native_csom_field_keeps_friendly_name_and_analysis_separate_from_url()
    {
        var page = Page(Guid.NewGuid());
        page.WebPartCount = 7;
        page.MappingPercentage = 42;
        page.AssessmentStatus = "Failed";
        PageWebPartExtractor.ApplyPublishingMetadata(page, Fields(new FieldUrlValue
        {
            Url = Site + "/_catalogs/masterpage/Article Left.aspx", Description = "ArticleLeft",
        }));
        page.Layout.Should().Be("ArticleLeft");
        page.LayoutUrl.Should().Be(LayoutUrl);
        page.LayoutReferenceStatus.Should().Be("Unresolved");
        page.LayoutReferenceReason.Should().Be("InventoryPending");
        page.PageType.Should().Be("PublishingPage");
        page.WebPartCount.Should().Be(7);
        page.MappingPercentage.Should().Be(42);
        page.AssessmentStatus.Should().Be("Failed");
    }

    [Theory]
    [InlineData("absent", "ReferenceMetadataMissing")]
    [InlineData("null", "ReferenceMetadataMissing")]
    [InlineData("description", "ReferenceMetadataUnusable")]
    [InlineData("string", "ReferenceMetadataUnusable")]
    [InlineData("foreign", "ReferenceMetadataUnusable")]
    public async Task Missing_or_unusable_metadata_never_fabricates_a_url_or_loses_the_page(string input, string reason)
    {
        var page = Page(Guid.NewGuid());
        using (var db = database.CreateContext()) await LayoutRoutingEvidence.RecordAuthorityAsync(db, page.ScanId);
        object value = input switch
        {
            "description" => new FieldUrlValue { Description = "ArticleLeft" },
            "string" => "ArticleLeft",
            "foreign" => new FieldUrlValue { Url = "https://other.example/layout.aspx", Description = "ArticleLeft" },
            _ => null,
        };
        PageWebPartExtractor.ApplyPublishingMetadata(page, input == "absent" ? new() : Fields(value));
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { await Target(page.ScanId) });
        await Store(page);
        await writer.FinalizeScanAsync(page.ScanId);
        var stored = await Read(page.ScanId);
        stored.LayoutUrl.Should().BeNull();
        stored.LayoutReferenceStatus.Should().Be("Unresolved");
        stored.LayoutReferenceReason.Should().Be(reason);
        stored.PageType.Should().Be("PublishingPage");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cross_web_relationship_waits_for_scan_inventory_in_either_order_and_round_trips_native_csv(bool pageFirst)
    {
        var page = await CapturedScanPage();
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var target = await Target(page.ScanId);
        target.Url = "/sites/layouts/_catalogs/masterpage/article left.aspx";
        if (pageFirst) await Store(page);
        await writer.WriteAsync(new[] { target });
        if (!pageFirst) await Store(page);
        (await Read(page.ScanId)).LayoutReferenceReason.Should().Be("InventoryPending");
        await writer.FinalizeScanAsync(page.ScanId);
        var stored = await Read(page.ScanId);
        AssertResolved(stored);

        var directory = Path.Combine(Path.GetTempPath(), "layout-reference-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var db = database.CreateContext();
            await ReportManager.ExportClassicReportDataAsync(db, page.ScanId, directory, new CsvConfiguration(CultureInfo.InvariantCulture));
            using var csv = new CsvReader(new StreamReader(Path.Combine(directory, "classicpages.csv")), CultureInfo.InvariantCulture);
            AssertResolved(csv.GetRecords<ClassicPage>().Single());
            using var discoveryCsv = new CsvReader(new StreamReader(Path.Combine(directory, "discovery.csv")), CultureInfo.InvariantCulture);
            var asset = discoveryCsv.GetRecords<ClassicPageDiscovery>().Single(row => row.RowType == "Page");
            asset.WebUrl.Should().NotBe(stored.WebUrl);
            asset.ScanId.Should().Be(stored.ScanId);
            asset.SiteCollectionId.Should().Be(stored.SiteCollectionId);
            asset.AssetPurpose.Should().Be("PageLayout");
            PublishingLayoutTypeEvidence.IsConfirmedMember(asset).Should().BeTrue();
            asset.PageTypeEvidenceJson.Should().Be(target.PageTypeEvidenceJson);
            PublishingLayoutReference.NormalizeUrl(asset.Url, asset.SiteUrl).Should().BeEquivalentTo(stored.LayoutUrl);
        }
        finally { Directory.Delete(directory, recursive: true); }

        static void AssertResolved(ClassicPage actual)
        {
            actual.LayoutUrl.Should().Be(LayoutUrl);
            actual.Layout.Should().Be("ArticleLeft");
            actual.LayoutReferenceStatus.Should().Be("Resolved");
            actual.LayoutReferenceReason.Should().Be("ConfirmedPublishingLayoutFamily");
            actual.PageType.Should().Be("PublishingPage");
            actual.WebPartCount.Should().Be(3);
        }
    }

    [Theory]
    [InlineData("Root", true)]
    [InlineData("Root", false)]
    [InlineData("Direct", true)]
    [InlineData("Direct", false)]
    [InlineData("Indirect", true)]
    [InlineData("Indirect", false)]
    public async Task Outside_catalog_family_needs_no_content_type_or_purpose_confirmation(string type, bool pageFirst)
    {
        const string url = "/sites/layouts/custom/Article.aspx";
        var page = await CapturedScanPage(url);
        var target = await Target(page.ScanId, type);
        target.Url = "https://CONTOSO.sharepoint.com" + url.ToUpperInvariant();
        target.SiteUrl = Site.ToUpperInvariant() + "/";
        target.ContentTypeId = null;
        target.AssetPurpose = target.AssetPurposeStatus = "Unknown";
        target.AssetPurposeReason = "ContentTypeUnavailable";
        // Exercise the consumer directly with stale purpose projections: it must not need a
        // writer to turn CLR evidence into purpose confirmation before resolving the reference.
        using (var db = database.CreateContext())
        {
            if (pageFirst) await StorageManager.StorePageInformationAsync(db, new() { page });
            db.ClassicPageDiscoveries.Add(target);
            await db.SaveChangesAsync();
            if (!pageFirst) await StorageManager.StorePageInformationAsync(db, new() { page });
        }
        (await Read(page.ScanId)).LayoutReferenceReason.Should().Be("InventoryPending");
        await new AssessmentDiscoveryWriter(database.CreateContext).FinalizeScanAsync(page.ScanId);
        var stored = await Read(page.ScanId);
        stored.LayoutUrl.Should().Be(url);
        stored.Layout.Should().Be("ArticleLeft");
        stored.LayoutReferenceStatus.Should().Be("Resolved");
        stored.LayoutReferenceReason.Should().Be("ConfirmedPublishingLayoutFamily");
        stored.WebPartCount.Should().Be(3);
        using var read = database.CreateContext();
        var unchanged = await read.ClassicPageDiscoveries.SingleAsync(row => row.ScanId == page.ScanId && row.RowType == "Page");
        unchanged.Should().BeEquivalentTo(target);
    }

    [Theory]
    [InlineData("Outside", "TargetNotPublishingLayoutFamily")]
    [InlineData("Incomplete", "TargetTypeFamilyUnknown")]
    [InlineData("Missing", "TargetTypeFamilyUnknown")]
    [InlineData(null, "TargetTypeSourceUnknown")]
    public async Task Stale_confirmed_purpose_cannot_substitute_for_confirmed_family(string type, string reason)
    {
        var page = await CapturedScanPage();
        var target = await Target(page.ScanId, type);
        using (var db = database.CreateContext())
        {
            db.ClassicPageDiscoveries.Add(target);
            await db.SaveChangesAsync();
        }
        await Store(page);
        await new AssessmentDiscoveryWriter(database.CreateContext).FinalizeScanAsync(page.ScanId);
        var stored = await Read(page.ScanId);
        stored.LayoutUrl.Should().Be(LayoutUrl);
        stored.LayoutReferenceStatus.Should().Be("Unresolved");
        stored.LayoutReferenceReason.Should().Be(reason);
        using var read = database.CreateContext();
        var unchanged = await read.ClassicPageDiscoveries.SingleAsync(row => row.ScanId == page.ScanId && row.RowType == "Page");
        unchanged.AssetPurpose.Should().Be("PageLayout");
        unchanged.AssetPurposeStatus.Should().Be("Confirmed");
        unchanged.Should().BeEquivalentTo(target);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Site_url_still_matches_when_an_optional_site_id_is_unavailable(bool pageIdMissing, bool targetIdMissing)
    {
        var page = await CapturedScanPage();
        var target = await Target(page.ScanId);
        if (pageIdMissing) page.SiteCollectionId = null;
        if (targetIdMissing) target.SiteCollectionId = null;
        await Store(page);
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { target });
        await writer.FinalizeScanAsync(page.ScanId);
        (await Read(page.ScanId)).LayoutReferenceStatus.Should().Be("Resolved");
    }

    [Theory]
    [InlineData("absent", "TargetNotDiscovered")]
    [InlineData("scan", "TargetNotDiscovered")]
    [InlineData("siteUrl", "TargetNotDiscovered")]
    [InlineData("siteId", "TargetNotDiscovered")]
    [InlineData("referenceRow", "TargetNotDiscovered")]
    [InlineData("unknownType", "TargetTypeFamilyUnknown")]
    [InlineData("nonMember", "TargetNotPublishingLayoutFamily")]
    [InlineData("contentTypeOnly", "TargetTypeSourceUnknown")]
    [InlineData("origin", "TargetTypeEvidenceUnavailable")]
    [InlineData("ambiguous", "TargetAmbiguous")]
    public async Task Unresolved_inventory_relationship_retains_known_url(string scenario, string reason)
    {
        var page = await CapturedScanPage();
        var target = await Target(page.ScanId, scenario == "nonMember" ? "Outside" : scenario == "unknownType" ? "Incomplete" : "Root");
        switch (scenario)
        {
            case "scan": target.ScanId = Guid.NewGuid(); break;
            case "siteUrl": target.SiteUrl = "https://contoso.sharepoint.com/sites/other"; break;
            case "siteId": target.SiteCollectionId = Guid.NewGuid(); break;
            case "referenceRow": target.RowType = "Reference"; break;
            case "contentTypeOnly":
                target.PageTypeEvidenceJson = null;
                target.PageTypeEvidenceOrigin = "None";
                target.PageTypeSourceStatus = target.PageTypeResolutionStatus = target.PublishingLayoutFamily = "Unknown";
                break;
            case "origin": target.PageTypeEvidenceOrigin = "None"; break;
        }
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        if (scenario != "absent") await writer.WriteAsync(new[] { target });
        if (scenario == "ambiguous") await writer.WriteAsync(new[] { await Target(page.ScanId) });
        await Store(page);
        await writer.FinalizeScanAsync(page.ScanId);
        var stored = await Read(page.ScanId);
        stored.LayoutUrl.Should().Be(LayoutUrl);
        stored.LayoutReferenceStatus.Should().Be("Unresolved");
        stored.LayoutReferenceReason.Should().Be(reason);
    }

    [Theory]
    [InlineData("Denied", true)]
    [InlineData("Failed", true)]
    [InlineData("Unknown", true)]
    [InlineData("Denied", false)]
    [InlineData("Failed", false)]
    [InlineData("Unknown", false)]
    public async Task Target_failure_is_not_success_and_inventory_evidence_is_unchanged(string status, bool discovery)
    {
        var page = await CapturedScanPage();
        var target = await Target(page.ScanId);
        if (discovery) target.DiscoveryStatus = status;
        else target.AssessmentStatus = status;
        target.ErrorDetail = "Retained acquisition evidence";
        target.ErrorCodes = "MetadataUnavailable";
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { target });
        await Store(page);
        await writer.FinalizeScanAsync(page.ScanId);
        var stored = await Read(page.ScanId);
        stored.LayoutUrl.Should().Be(LayoutUrl);
        stored.LayoutReferenceStatus.Should().Be("Unresolved");
        stored.LayoutReferenceReason.Should().Be("Target" + (discovery ? "Discovery" : "Assessment") + status);
        using var db = database.CreateContext();
        var unchanged = await db.ClassicPageDiscoveries.SingleAsync(row => row.ScanId == page.ScanId && row.RowType == "Page");
        unchanged.ErrorDetail.Should().Be(target.ErrorDetail);
        unchanged.ErrorCodes.Should().Be(target.ErrorCodes);
        unchanged.DiscoveryStatus.Should().Be(target.DiscoveryStatus);
        unchanged.AssessmentStatus.Should().Be(target.AssessmentStatus);
        unchanged.AssetPurpose.Should().Be("PageLayout");
    }

    [Theory]
    [InlineData("Denied")]
    [InlineData("Failed")]
    [InlineData("Unknown")]
    public async Task Target_source_failure_survives_later_success_and_reference_finalization(string status)
    {
        var page = await CapturedScanPage();
        var target = await Target(page.ScanId);
        await PublishingLayoutTypeEvidence.AcquireAsync(target, (_, _) => status switch
        {
            "Denied" => throw new UnauthorizedAccessException(),
            "Failed" => throw new IOException(),
            _ => Task.FromResult<string>(null),
        }, new(), default);
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { target });
        await LayoutRoutingEvidence.InspectAsync(target);
        await writer.WriteAsync(new[] { target });
        await Store(page);
        await writer.FinalizeScanAsync(page.ScanId);
        var stored = await Read(page.ScanId);
        stored.LayoutUrl.Should().Be(LayoutUrl);
        stored.LayoutReferenceStatus.Should().Be("Unresolved");
        stored.LayoutReferenceReason.Should().Be("TargetTypeSource" + status);
        using var db = database.CreateContext();
        var unchanged = await db.ClassicPageDiscoveries.SingleAsync(row => row.ScanId == page.ScanId && row.RowType == "Page");
        unchanged.PageTypeSourceStatus.Should().Be(status);
        unchanged.PageTypeReason.Should().Be(target.PageTypeReason);
        unchanged.PageTypeEvidenceJson.Should().Be(target.PageTypeEvidenceJson);
        PublishingLayoutTypeEvidence.IsConfirmedMember(unchanged).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "ReferenceMetadataMissing")]
    [InlineData("ArticleLeft", "ReferenceMetadataUnusable")]
    public async Task Unusable_pending_url_cannot_match_an_unusable_inventory_locator(string url, string reason)
    {
        var page = await CapturedScanPage();
        page.LayoutUrl = url;
        var target = await Target(page.ScanId);
        target.Url = url;
        await new AssessmentDiscoveryWriter(database.CreateContext).WriteAsync(new[] { target });
        await Store(page);
        await new AssessmentDiscoveryWriter(database.CreateContext).FinalizeScanAsync(page.ScanId);
        var stored = await Read(page.ScanId);
        stored.LayoutUrl.Should().Be(url);
        stored.LayoutReferenceStatus.Should().Be("Unresolved");
        stored.LayoutReferenceReason.Should().Be(reason);
    }

    [Theory]
    [InlineData("Denied", false)]
    [InlineData("Failed", false)]
    [InlineData("Unknown", false)]
    [InlineData("Denied", true)]
    [InlineData("Failed", true)]
    [InlineData("Unknown", true)]
    public async Task Reference_acquisition_failure_survives_reread_and_finalization(string status, bool failureFirst)
    {
        var page = await CapturedScanPage();
        if (failureFirst) page.LayoutUrl = null;
        if (status == "Unknown") PublishingLayoutReference.RetainUnavailable(page, "ReferenceMetadataUnknown");
        else PublishingLayoutReference.RecordFailure(page, status == "Denied" ? new UnauthorizedAccessException() : new IOException());
        PublishingLayoutReference.Capture(page, Fields(new FieldUrlValue { Url = LayoutUrl }));
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { await Target(page.ScanId) });
        await Store(page);
        await writer.FinalizeScanAsync(page.ScanId);
        var stored = await Read(page.ScanId);
        stored.LayoutUrl.Should().Be(LayoutUrl);
        stored.LayoutReferenceStatus.Should().Be("Unresolved");
        stored.LayoutReferenceReason.Should().Contain("ReferenceMetadata" + status);
    }

    [Fact]
    public void Sparse_or_conflicting_reference_cannot_erase_a_known_url()
    {
        var page = CapturedPage();
        PublishingLayoutReference.Capture(page, new Dictionary<string, object>());
        page.LayoutUrl.Should().Be(LayoutUrl);
        page.LayoutReferenceReason.Should().Be("ReferenceMetadataMissing");
        PublishingLayoutReference.Capture(page, Fields(new FieldUrlValue { Url = "/sites/layouts/other.aspx" }));
        page.LayoutUrl.Should().Be(LayoutUrl);
        page.LayoutReferenceReason.Should().Contain("ReferenceMetadataConflict");
        PublishingLayoutReference.Capture(page, Fields(new FieldUrlValue { Url = LayoutUrl }));
        page.LayoutReferenceStatus.Should().Be("Unresolved");
        page.LayoutReferenceReason.Should().Contain("ReferenceMetadataConflict");
    }

    [Theory]
    [InlineData("Denied")]
    [InlineData("Failed")]
    [InlineData("Unknown")]
    public async Task A_resolved_relationship_does_not_change_the_content_pages_acquisition_or_analysis_evidence(string status)
    {
        var page = await CapturedScanPage();
        page.DiscoveryStatus = page.AssessmentStatus = status;
        page.MappingPercentage = 42;
        page.UnmappedWebParts = "Retained unmapped type";
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await Store(page);
        await writer.WriteAsync(new[] { await Target(page.ScanId) });
        await writer.FinalizeScanAsync(page.ScanId);
        var stored = await Read(page.ScanId);
        stored.LayoutReferenceStatus.Should().Be("Resolved");
        stored.DiscoveryStatus.Should().Be(status);
        stored.AssessmentStatus.Should().Be(status);
        stored.WebPartCount.Should().Be(3);
        stored.MappingPercentage.Should().Be(42);
        stored.UnmappedWebParts.Should().Be("Retained unmapped type");
    }

    [Theory]
    [InlineData("Denied", "ReferenceMetadataDenied")]
    [InlineData("Outside", "TargetNotPublishingLayoutFamily")]
    [InlineData("Incomplete", "TargetTypeFamilyUnknown")]
    public async Task Native_csv_exports_unresolved_known_url_and_acquisition_reason(string scenario, string reason)
    {
        var page = await CapturedScanPage();
        if (scenario == "Denied") PublishingLayoutReference.RecordFailure(page, new UnauthorizedAccessException());
        else await new AssessmentDiscoveryWriter(database.CreateContext).WriteAsync(new[] { await Target(page.ScanId, scenario) });
        await Store(page);
        await new AssessmentDiscoveryWriter(database.CreateContext).FinalizeScanAsync(page.ScanId);
        var directory = Path.Combine(Path.GetTempPath(), "layout-unresolved-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var db = database.CreateContext();
            await ReportManager.ExportClassicReportDataAsync(db, page.ScanId, directory, new CsvConfiguration(CultureInfo.InvariantCulture));
            using var csv = new CsvReader(new StreamReader(Path.Combine(directory, "classicpages.csv")), CultureInfo.InvariantCulture);
            var exported = csv.GetRecords<ClassicPage>().Single();
            exported.LayoutUrl.Should().Be(LayoutUrl);
            exported.Layout.Should().Be("ArticleLeft");
            exported.LayoutReferenceStatus.Should().Be("Unresolved");
            exported.LayoutReferenceReason.Should().Be(reason);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Additive_upgrade_from_asset_schema_preserves_historical_values_and_defaults_without_repair()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        using var db = new ScanContext(new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260924172015_ClassicAssetPurpose");
        var scan = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ClassicPages
            (ScanId, SiteUrl, WebUrl, PageUrl, ListId, PageType, Layout, AssessmentStatus, ModifiedAt, UncustomizedHomePage, WebPartCount, MappingPercentage)
            VALUES ({scan}, {Site}, '/sites/layouts/subweb', '/sites/layouts/subweb/Pages/old.aspx', {Guid.Empty},
                'PublishingPage', 'HistoricalLayout', 'Failed', {DateTime.MinValue}, 0, 7, 42)");
        await migrator.MigrateAsync();
        var page = await db.ClassicPages.SingleAsync();
        page.LayoutUrl.Should().BeNull();
        page.LayoutReferenceStatus.Should().Be("Unknown");
        page.LayoutReferenceReason.Should().Be("NotEvaluated");
        await PublishingLayoutReference.FinalizeAsync(db, scan, new[] { await Target(scan) }, default);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        page = await db.ClassicPages.SingleAsync();
        page.LayoutUrl.Should().BeNull();
        page.LayoutReferenceStatus.Should().Be("Unknown");
        page.LayoutReferenceReason.Should().Be("NotEvaluated");
        page.PageType.Should().Be("PublishingPage");
        page.Layout.Should().Be("HistoricalLayout");
        page.AssessmentStatus.Should().Be("Failed");
        page.WebPartCount.Should().Be(7);
        page.MappingPercentage.Should().Be(42);

        var migration = new PnP.Scanning.Core.Storage.DatabaseMigration.ClassicPublishingLayoutReference();
        migration.UpOperations.Should().HaveCount(3).And.OnlyContain(operation => operation is AddColumnOperation);
        var columns = migration.UpOperations.Cast<AddColumnOperation>().ToArray();
        columns.Should().OnlyContain(column => column.Table == "ClassicPages");
        columns.Single(column => column.Name == "LayoutUrl").IsNullable.Should().BeTrue();
        columns.Single(column => column.Name == "LayoutReferenceStatus").DefaultValue.Should().Be("Unknown");
        columns.Single(column => column.Name == "LayoutReferenceReason").DefaultValue.Should().Be("NotEvaluated");
    }

    [Theory]
    [InlineData("Unresolved", "InventoryPending")]
    [InlineData("Resolved", "ConfirmedLayoutAsset")]
    [InlineData("Unresolved", "ReferenceMetadataDenied")]
    public async Task Upgrade_does_not_repair_stored_or_pending_legacy_references(string status, string reason)
    {
        var scan = Guid.NewGuid();
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var db = new ScanContext(new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260924173030_ClassicPublishingLayoutReference");
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO Scans
            (ScanId, StartDate, EndDate, Status, PreScanStatus, PostScanStatus, CLIThreads)
            VALUES ({scan}, '2026-01-01', '2026-01-01', 0, 0, 0, 1)");
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ClassicPages
            (ScanId, SiteUrl, WebUrl, PageUrl, ListId, PageType, Layout, LayoutUrl, LayoutReferenceStatus,
             LayoutReferenceReason, AssessmentStatus, ModifiedAt, UncustomizedHomePage, WebPartCount, MappingPercentage)
            VALUES ({scan}, {Site}, '/sites/layouts/subweb', '/sites/layouts/subweb/Pages/old.aspx', {Guid.Empty},
                'PublishingPage', 'HistoricalLayout', {LayoutUrl}, {status}, {reason}, 'Failed', {DateTime.MinValue}, 0, 7, 42)");
        await migrator.MigrateAsync();
        (await db.Scans.SingleAsync()).PublishingLayoutRuleVersion.Should().Be(0);
        var before = await db.ClassicPages.AsNoTracking().SingleAsync();
        await PublishingLayoutReference.FinalizeAsync(db, scan, new[] { await Target(scan) }, default);
        await db.SaveChangesAsync();
        (await db.ClassicPages.AsNoTracking().SingleAsync()).Should().BeEquivalentTo(before);
        var directory = Path.Combine(Path.GetTempPath(), "layout-reference-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await ReportManager.ExportClassicReportDataAsync(db, scan, directory, new CsvConfiguration(CultureInfo.InvariantCulture));
            using var csv = new CsvReader(new StreamReader(Path.Combine(directory, "classicpages.csv")), CultureInfo.InvariantCulture);
            var exported = csv.GetRecords<ClassicPage>().Single();
            exported.Layout.Should().Be(before.Layout);
            exported.LayoutUrl.Should().Be(LayoutUrl);
            exported.LayoutReferenceStatus.Should().Be(status);
            exported.LayoutReferenceReason.Should().Be(reason);
            exported.AssessmentStatus.Should().Be("Failed");
            exported.WebPartCount.Should().Be(7);
            exported.MappingPercentage.Should().Be(42);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(0, "Unresolved", "InventoryPending")]
    [InlineData(1, "Resolved", "ConfirmedPublishingLayoutFamily")]
    [InlineData(2, "Unresolved", "InventoryPending")]
    public async Task Restart_and_finalization_obey_recorded_authority(int version, string status, string reason)
    {
        var page = CapturedPage();
        var storage = new StorageManager(new EphemeralDataProtectionProvider(), new ConfigurationBuilder().Build());
        await storage.LaunchNewScanAsync(page.ScanId,
            new StartRequest { Mode = "Classic", AuthMode = "application", Threads = 1 }, new());
        using (var db = new ScanContext(page.ScanId))
        {
            (await db.Scans.SingleAsync()).PublishingLayoutRuleVersion.Should().Be(1);
            (await db.Scans.SingleAsync()).PublishingLayoutRuleVersion = version;
            db.ClassicPageDiscoveries.Add(await Target(page.ScanId));
            await db.SaveChangesAsync();
            await StorageManager.StorePageInformationAsync(db, new() { page });
        }
        await storage.ConsolidatedScanToEnableRestartAsync(page.ScanId);
        await storage.RestartScanAsync(page.ScanId);
        using (var db = new ScanContext(page.ScanId))
        {
            (await db.Scans.SingleAsync()).PublishingLayoutRuleVersion.Should().Be(version);
            (await db.ClassicPages.SingleAsync()).LayoutReferenceReason.Should().Be("InventoryPending");
        }
        var writer = new AssessmentDiscoveryWriter(page.ScanId);
        await writer.FinalizeScanAsync(page.ScanId);
        await writer.FinalizeScanAsync(page.ScanId);
        using var read = new ScanContext(page.ScanId);
        var stored = await read.ClassicPages.SingleAsync();
        stored.LayoutReferenceStatus.Should().Be(status);
        stored.LayoutReferenceReason.Should().Be(reason);
        stored.LayoutUrl.Should().Be(LayoutUrl);
        stored.Layout.Should().Be(page.Layout);
        stored.PageType.Should().Be(page.PageType);
        stored.WebPartCount.Should().Be(page.WebPartCount);
    }

    [Fact]
    public async Task Missing_scan_authority_does_not_finalize_a_pending_reference()
    {
        var page = CapturedPage();
        await Store(page);
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { await Target(page.ScanId) });
        await writer.FinalizeScanAsync(page.ScanId);
        (await Read(page.ScanId)).Should().BeEquivalentTo(page);
    }

    private static Dictionary<string, object> Fields(object value) => new() { [PublishingLayoutReference.FieldName] = value };

    private static ClassicPage Page(Guid scan) => new()
    {
        ScanId = scan, SiteUrl = Site, SiteCollectionId = SiteId,
        WebUrl = "/sites/layouts/subweb", WebId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        PageUrl = "/sites/layouts/subweb/Pages/article.aspx", PageType = "PublishingPage",
    };

    private static ClassicPage CapturedPage(string url = LayoutUrl)
    {
        var page = Page(Guid.NewGuid());
        PageWebPartExtractor.ApplyPublishingMetadata(page, Fields(new FieldUrlValue { Url = url, Description = "ArticleLeft" }));
        page.WebPartCount = 3;
        return page;
    }

    private async Task<ClassicPage> CapturedScanPage(string url = LayoutUrl)
    {
        var page = CapturedPage(url);
        using var db = database.CreateContext();
        await LayoutRoutingEvidence.RecordAuthorityAsync(db, page.ScanId);
        return page;
    }

    private static async Task<ClassicPageDiscovery> Target(Guid scan, string type = "Root")
    {
        var target = new ClassicPageDiscovery
        {
            ScanId = scan, SiteUrl = Site, SiteCollectionId = SiteId, WebUrl = "/sites/layouts", WebId = Guid.NewGuid(),
            Url = LayoutUrl, RowType = "Page", RecordKey = Guid.NewGuid().ToString(), DiscoveryStatus = "Discovered",
            ContentTypeId = AspxAssetPurpose.LayoutContentType,
            AssetPurpose = "PageLayout", AssetPurposeStatus = "Confirmed", AssetPurposeReason = "PageLayoutContentType",
        };
        await LayoutRoutingEvidence.InspectAsync(target, type);
        return target;
    }

    private async Task Store(ClassicPage page)
    {
        using var db = database.CreateContext();
        await StorageManager.StorePageInformationAsync(db, new List<ClassicPage> { page });
    }

    private async Task<ClassicPage> Read(Guid scan)
    {
        using var db = database.CreateContext();
        return await db.ClassicPages.SingleAsync(page => page.ScanId == scan);
    }
}
