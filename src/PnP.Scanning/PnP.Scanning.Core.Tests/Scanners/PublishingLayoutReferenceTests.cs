using CsvHelper;
using CsvHelper.Configuration;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.SharePoint.Client;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
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
        object value = input switch
        {
            "description" => new FieldUrlValue { Description = "ArticleLeft" },
            "string" => "ArticleLeft",
            "foreign" => new FieldUrlValue { Url = "https://other.example/layout.aspx", Description = "ArticleLeft" },
            _ => null,
        };
        PageWebPartExtractor.ApplyPublishingMetadata(page, input == "absent" ? new() : Fields(value));
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { Target(page.ScanId) });
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
        var page = CapturedPage();
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var target = Target(page.ScanId);
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
            PublishingLayoutReference.NormalizeUrl(asset.Url, asset.SiteUrl).Should().BeEquivalentTo(stored.LayoutUrl);
        }
        finally { Directory.Delete(directory, recursive: true); }

        static void AssertResolved(ClassicPage actual)
        {
            actual.LayoutUrl.Should().Be(LayoutUrl);
            actual.Layout.Should().Be("ArticleLeft");
            actual.LayoutReferenceStatus.Should().Be("Resolved");
            actual.LayoutReferenceReason.Should().Be("ConfirmedLayoutAsset");
            actual.PageType.Should().Be("PublishingPage");
            actual.WebPartCount.Should().Be(3);
        }
    }

    [Theory]
    [InlineData("absent", "TargetNotDiscovered")]
    [InlineData("scan", "TargetNotDiscovered")]
    [InlineData("siteUrl", "TargetNotDiscovered")]
    [InlineData("siteId", "TargetNotDiscovered")]
    [InlineData("referenceRow", "TargetNotDiscovered")]
    [InlineData("unknownPurpose", "TargetPurposeUnavailable")]
    [InlineData("otherPurpose", "TargetNotLayoutAsset")]
    [InlineData("ambiguous", "TargetAmbiguous")]
    public async Task Unresolved_inventory_relationship_retains_known_url(string scenario, string reason)
    {
        var page = CapturedPage();
        var target = Target(page.ScanId);
        switch (scenario)
        {
            case "scan": target.ScanId = Guid.NewGuid(); break;
            case "siteUrl": target.SiteUrl = "https://contoso.sharepoint.com/sites/other"; break;
            case "siteId": target.SiteCollectionId = Guid.NewGuid(); break;
            case "referenceRow": target.RowType = "Reference"; break;
            case "unknownPurpose": target.AssetPurposeStatus = "Unknown"; target.AssetPurposeReason = "ContentTypeUnavailable"; break;
            case "otherPurpose": target.AssetPurpose = "ContentPage"; break;
        }
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        if (scenario != "absent") await writer.WriteAsync(new[] { target });
        if (scenario == "ambiguous") await writer.WriteAsync(new[] { Target(page.ScanId) });
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
        var page = CapturedPage();
        var target = Target(page.ScanId);
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
    public async Task Reference_acquisition_failure_survives_reread_and_finalization(string status)
    {
        var page = CapturedPage();
        if (status == "Unknown") PublishingLayoutReference.RetainUnavailable(page, "ReferenceMetadataUnknown");
        else PublishingLayoutReference.RecordFailure(page, status == "Denied" ? new UnauthorizedAccessException() : new IOException());
        PublishingLayoutReference.Capture(page, Fields(new FieldUrlValue { Url = LayoutUrl }));
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { Target(page.ScanId) });
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
        var page = CapturedPage();
        page.DiscoveryStatus = page.AssessmentStatus = status;
        page.MappingPercentage = 42;
        page.UnmappedWebParts = "Retained unmapped type";
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await Store(page);
        await writer.WriteAsync(new[] { Target(page.ScanId) });
        await writer.FinalizeScanAsync(page.ScanId);
        var stored = await Read(page.ScanId);
        stored.LayoutReferenceStatus.Should().Be("Resolved");
        stored.DiscoveryStatus.Should().Be(status);
        stored.AssessmentStatus.Should().Be(status);
        stored.WebPartCount.Should().Be(3);
        stored.MappingPercentage.Should().Be(42);
        stored.UnmappedWebParts.Should().Be("Retained unmapped type");
    }

    [Fact]
    public async Task Native_csv_exports_unresolved_known_url_and_acquisition_reason()
    {
        var page = CapturedPage();
        PublishingLayoutReference.RecordFailure(page, new UnauthorizedAccessException());
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
            exported.LayoutReferenceReason.Should().Be("ReferenceMetadataDenied");
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
        await PublishingLayoutReference.FinalizeAsync(db, scan, new[] { Target(scan) }, default);
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

    private static Dictionary<string, object> Fields(object value) => new() { [PublishingLayoutReference.FieldName] = value };

    private static ClassicPage Page(Guid scan) => new()
    {
        ScanId = scan, SiteUrl = Site, SiteCollectionId = SiteId,
        WebUrl = "/sites/layouts/subweb", WebId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        PageUrl = "/sites/layouts/subweb/Pages/article.aspx", PageType = "PublishingPage",
    };

    private static ClassicPage CapturedPage()
    {
        var page = Page(Guid.NewGuid());
        PageWebPartExtractor.ApplyPublishingMetadata(page, Fields(new FieldUrlValue { Url = LayoutUrl, Description = "ArticleLeft" }));
        page.WebPartCount = 3;
        return page;
    }

    private static ClassicPageDiscovery Target(Guid scan) => new()
    {
        ScanId = scan, SiteUrl = Site, SiteCollectionId = SiteId, WebUrl = "/sites/layouts", WebId = Guid.NewGuid(),
        Url = LayoutUrl, RowType = "Page", RecordKey = Guid.NewGuid().ToString(), DiscoveryStatus = "Discovered",
        AssetPurpose = "PageLayout", AssetPurposeStatus = "Confirmed", AssetPurposeReason = "PageLayoutContentType",
    };

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
