using CsvHelper;
using CsvHelper.Configuration;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Globalization;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed class AspxAssetPurposeTests : IClassFixture<ScanContextFixture>
{
    private const string Site = "https://contoso.sharepoint.com/sites/purpose";
    private const string Web = "/sites/purpose";
    private readonly ScanContextFixture database;

    public AspxAssetPurposeTests(ScanContextFixture database) => this.database = database;

    [Theory]
    [InlineData("_catalogs/masterpage/layout.aspx", false, false)]
    [InlineData("_catalogs/masterpage/layout.aspx", true, false)]
    [InlineData("Custom/layout.aspx", false, false)]
    [InlineData("Custom/layout.aspx", true, true)]
    public void Layout_content_type_not_location_proves_an_asset(string path, bool descendant, bool lowerCase)
    {
        var contentType = AspxAssetPurpose.LayoutContentType + (descendant ? "00AABBCCDDEEFF00112233445566778899" : "");
        if (lowerCase) contentType = contentType.ToLowerInvariant();
        var row = Row(Guid.NewGuid(), path, contentType);
        row.AssetPurpose.Should().Be("PageLayout");
        row.AssetPurposeStatus.Should().Be("Confirmed");
        row.AssetPurposeReason.Should().Be("PageLayoutContentType");
        row.PageType.Should().NotBe("PublishingPage");
        SharePointLiveAspxDiscoveryProvider.IsPublishingPageContentType(contentType).Should().BeFalse();
    }

    [Theory]
    [InlineData("Pages/article.aspx", false, false)]
    [InlineData("_catalogs/masterpage/article.aspx", true, true)]
    public void Actual_publishing_types_keep_their_page_family(string path, bool descendant, bool lowerCase)
    {
        var contentType = AspxAssetPurpose.PublishingContentType + (descendant ? "00AABBCCDDEEFF00112233445566778899" : "");
        if (lowerCase) contentType = contentType.ToLowerInvariant();
        var row = Row(Guid.NewGuid(), path, contentType);
        row.AssetPurpose.Should().Be("ContentPage");
        row.AssetPurposeStatus.Should().Be("Confirmed");
        row.PageType.Should().Be("PublishingPage");
        SharePointLiveAspxDiscoveryProvider.IsPublishingPageContentType(contentType).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, "ContentTypeUnavailable")]
    [InlineData("", "ContentTypeUnavailable")]
    [InlineData(" ", "ContentTypeUnavailable")]
    [InlineData("0x0101", "UnrecognizedContentType")]
    public void Catalog_path_does_not_supply_missing_purpose_evidence(string contentType, string reason)
    {
        var row = Row(Guid.NewGuid(), "_catalogs/masterpage/unknown.aspx", contentType);
        row.AssetPurpose.Should().Be("Unknown");
        row.AssetPurposeStatus.Should().Be("Unknown");
        row.AssetPurposeReason.Should().Be(reason);
        AspxAssetPurpose.IsLayout(row).Should().BeFalse();
    }

    [Theory]
    [InlineData("Denied", false)]
    [InlineData("Failed", false)]
    [InlineData("Unknown", false)]
    [InlineData("Denied", true)]
    [InlineData("Failed", true)]
    [InlineData("Unknown", true)]
    public async Task Reobservations_and_assessment_updates_retain_purpose_and_failure_evidence(string status, bool asset)
    {
        var scan = Guid.NewGuid();
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var row = Row(scan, "_catalogs/masterpage/file.aspx", asset ? AspxAssetPurpose.LayoutContentType : null);
        row.PageType = "WebPartPage"; // Independent page-family evidence must survive sparse updates.
        row.DiscoveryStatus = row.AssessmentStatus = status;
        row.ErrorStage = "Metadata";
        row.ErrorCodes = "MetadataUnavailable";
        row.ErrorDetail = "retained, \"metadata\"\nfailure";
        row.EvidenceJson = "{\"retained\":true}";
        await writer.WriteAsync(new[] { row });

        var sparse = Row(scan, "_catalogs/masterpage/file.aspx", null, row.FileUniqueId);
        await writer.WriteAsync(new[] { sparse });
        var staleUpdate = Row(scan, "_catalogs/masterpage/file.aspx", null, row.FileUniqueId);
        staleUpdate.AssessmentStatus = "Complete";
        await writer.UpdateExistingAsync(new[] { staleUpdate });
        var stored = (await writer.ReadPagesAsync(scan, Site, Web)).Single();
        AssertRetained(stored);

        var directory = Path.Combine(Path.GetTempPath(), "asset-purpose-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var db = database.CreateContext();
            await ReportManager.ExportClassicReportDataAsync(db, scan, directory, new CsvConfiguration(CultureInfo.InvariantCulture));
            using var csv = new CsvReader(new StreamReader(Path.Combine(directory, "discovery.csv")), CultureInfo.InvariantCulture);
            var exported = csv.GetRecords<ClassicPageDiscovery>().Single();
            AssertRetained(exported, checkPageType: false);
        }
        finally { Directory.Delete(directory, recursive: true); }

        void AssertRetained(ClassicPageDiscovery actual, bool checkPageType = true)
        {
            actual.AssetPurpose.Should().Be(asset ? "PageLayout" : "Unknown");
            actual.AssetPurposeStatus.Should().Be(asset ? "Confirmed" : "Unknown");
            actual.AssetPurposeReason.Should().Contain("ContentTypeUnavailable");
            if (asset) actual.AssetPurposeReason.Should().Contain("PageLayoutContentType");
            actual.DiscoveryStatus.Should().Be(status);
            actual.AssessmentStatus.Should().Be(status);
            actual.ErrorStage.Should().Contain("Metadata");
            actual.ErrorCodes.Should().Contain("MetadataUnavailable");
            actual.ErrorDetail.Should().Contain(row.ErrorDetail);
            actual.EvidenceJson.Should().Be(row.EvidenceJson);
            actual.FileUniqueId.Should().Be(row.FileUniqueId);
            actual.SiteCollectionId.Should().Be(row.SiteCollectionId);
            actual.Url.Should().Be(row.Url);
            if (checkPageType) actual.PageType.Should().Be("WebPartPage");
        }
    }

    [Fact]
    public async Task Content_type_casing_does_not_report_a_changed_file()
    {
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var row = Row(Guid.NewGuid(), "Custom/layout.aspx", AspxAssetPurpose.LayoutContentType);
        await writer.WriteAsync(new[] { row });
        var repeat = Row(row.ScanId, "Custom/layout.aspx", row.ContentTypeId.ToLowerInvariant(), row.FileUniqueId);
        await writer.WriteAsync(new[] { repeat });
        var stored = (await writer.ReadPagesAsync(row.ScanId, Site, Web)).Single();
        stored.AssetPurpose.Should().Be("PageLayout");
        stored.ErrorCodes.Should().NotContain(DiscoveryGapCodes.ChangedDuringScan);
    }

    [Fact]
    public async Task Sparse_successful_update_cannot_turn_a_confirmed_asset_into_a_completed_content_page()
    {
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var row = Row(Guid.NewGuid(), "Custom/layout.aspx", AspxAssetPurpose.LayoutContentType);
        await writer.WriteAsync(new[] { row });
        var update = Row(row.ScanId, "Custom/layout.aspx", null, row.FileUniqueId);
        update.AssessmentStatus = "Complete";
        await writer.UpdateExistingAsync(new[] { update });
        var stored = (await writer.ReadPagesAsync(row.ScanId, Site, Web)).Single();
        stored.AssetPurpose.Should().Be("PageLayout");
        stored.AssetPurposeStatus.Should().Be("Confirmed");
        stored.AssessmentStatus.Should().Be("ExcludedAsset");
    }

    [Fact]
    public async Task Additive_upgrade_defaults_to_unevaluated_without_repairing_historical_results()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options;
        using var db = new ScanContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260918022217_ClassicAspxDiscovery");
        var scan = Guid.NewGuid();
        var url = Web + "/_catalogs/masterpage/historical.aspx";
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ClassicPageDiscoveries
            (ScanId, RecordKey, RowType, DiscoveryStatus, AssessmentStatus, ObservedAtUtc, SiteUrl, WebUrl, Url, PageType, ContentTypeId, ErrorDetail)
            VALUES ({scan}, 'page:historical', 'Page', 'Unknown', 'Failed', {DateTime.UtcNow}, {Site}, {Web}, {url}, 'PublishingPage', {AspxAssetPurpose.LayoutContentType}, 'historical failure')");
        // Use the historical schema, not the current EF model's newly added reference columns.
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ClassicPages
            (ScanId, SiteUrl, WebUrl, PageUrl, ListId, PageType, Layout, AssessmentStatus, ModifiedAt, UncustomizedHomePage, WebPartCount, MappingPercentage)
            VALUES ({scan}, {Site}, {Web}, {url}, {Guid.Empty}, 'PublishingPage', 'HistoricalLayout', 'Failed', {DateTime.MinValue}, 0, 7, 0)");
        db.ChangeTracker.Clear();
        await migrator.MigrateAsync();

        var row = await db.ClassicPageDiscoveries.SingleAsync();
        row.AssetPurpose.Should().Be("Unknown");
        row.AssetPurposeStatus.Should().Be("Unknown");
        row.AssetPurposeReason.Should().Be("NotEvaluated");
        row.PageType.Should().Be("PublishingPage");
        row.ContentTypeId.Should().Be(AspxAssetPurpose.LayoutContentType);
        row.DiscoveryStatus.Should().Be("Unknown");
        row.AssessmentStatus.Should().Be("Failed");
        row.ErrorDetail.Should().Be("historical failure");
        var page = await db.ClassicPages.SingleAsync();
        page.PageType.Should().Be("PublishingPage");
        page.Layout.Should().Be("HistoricalLayout");
        page.WebPartCount.Should().Be(7);
        page.AssessmentStatus.Should().Be("Failed");

        var migration = new PnP.Scanning.Core.Storage.DatabaseMigration.ClassicAssetPurpose();
        migration.UpOperations.Should().HaveCount(3).And.OnlyContain(operation => operation is AddColumnOperation);
        migration.UpOperations.Cast<AddColumnOperation>().Should().OnlyContain(column =>
            column.Table == "ClassicPageDiscoveries" && !column.IsNullable && column.DefaultValue != null);
    }

    private static ClassicPageDiscovery Row(Guid scan, string path, string contentType, Guid? fileId = null)
    {
        var id = fileId ?? Guid.NewGuid();
        var record = new RawDiscoveryRecord("file", id.ToString(), "library", Path.GetFileName(path),
            Web + "/" + path, true, "synthetic", SiteCollectionId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ContentTypeId: contentType, PageType: SharePointLiveAspxDiscoveryProvider.InferPageType(contentType));
        return AssessmentWebDiscovery.Page(scan, Site, Web,
            new("files", null, DiscoveryScopeKind.Folder, DiscoverySourceKind.RawListLibraryFiles, Web, "synthetic"), record);
    }
}
