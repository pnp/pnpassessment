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
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.DatabaseMigration;
using System.Globalization;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

public sealed partial class ClassicLayoutUpgradeTests
{
    [Theory]
    [InlineData("20260918022217_ClassicAspxDiscovery", false)]
    [InlineData("20260924173030_ClassicPublishingLayoutReference", true)]
    public async Task Both_historical_schemas_preserve_results_through_upgrade_restart_finalization_and_export(string baseline, bool hasLayouts)
    {
        const string site = "https://contoso.sharepoint.com/sites/history";
        const string web = "/sites/history";
        var scan = Guid.NewGuid();
        var states = new[] { "Denied", "Failed", "Unknown", "Complete" };
        var purposes = new[] { "PageLayout", "ContentPage", "Unknown", "PageLayout" };
        var referenceStates = new[] { "Resolved", "Unresolved", "Unresolved", "Unresolved" };
        var referenceReasons = new[] { "ConfirmedLayoutAsset", "InventoryPending", "ReferenceMetadataUnknown", "ReferenceMetadataDenied" };
        using var db = new ScanContext(scan);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(baseline);
        // LaunchNewScanAsync stores optional protobuf strings as empty rather than null.
        await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO Scans
            (ScanId, StartDate, EndDate, Status, PreScanStatus, PostScanStatus, CLIThreads, CLIMode, CLIAuthMode,
             CLITenant, CLIApplicationId, CLITenantId, CLIEnvironment, CLICertPath, CLICertFile, CLICertFilePassword)
            VALUES ({scan}, '2026-01-01', '2026-01-01', 0, 0, 0, 1, 'Classic', 'application', '', '', '', '', '', '', '')");
        for (int i = 0; i < states.Length; i++)
        {
            string state = states[i];
            string url = web + "/Pages/" + state + ".aspx";
            string error = "Historical, \"quoted\"\n" + state;
            var fileId = Guid.NewGuid();
            var modified = new DateTime(2026, 1, 2);
            string discoveryStatus = state == "Complete" ? "Discovered" : state;
            await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ClassicPageDiscoveries
                (ScanId, RecordKey, RowType, DiscoveryStatus, AssessmentStatus, ObservedAtUtc, SiteUrl, WebUrl,
                 Url, FileUniqueId, ListItemId, PageType, ContentTypeId, ErrorDetail, EvidenceJson)
                VALUES ({scan}, {"page:" + state}, 'Page', {discoveryStatus}, {state}, {modified}, {site}, {web},
                    {url}, {fileId}, 1, 'PublishingPage', {AspxAssetPurpose.LayoutContentType}, {error}, '{{""historical"":true}}')");
            await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ClassicPages
                (ScanId, SiteUrl, WebUrl, PageUrl, ListId, PageType, Layout, DiscoveryStatus, AssessmentStatus,
                 FileUniqueId, ListItemId, ModifiedAt, HomePage, UncustomizedHomePage, WebPartCount, MappingPercentage, UnmappedWebParts)
                VALUES ({scan}, {site}, {web}, {url}, {Guid.Empty}, 'PublishingPage', 'HistoricalLayout', {discoveryStatus},
                    {state}, {fileId}, 1, {modified}, NULL, 0, 7, 42, 'HistoricalUnmapped')");
            if (hasLayouts)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ClassicPageDiscoveries
                    SET AssetPurpose = {purposes[i]}, AssetPurposeStatus = {(i == 2 ? "Unknown" : "Confirmed")},
                        AssetPurposeReason = {(i == 0 || i == 3 ? "PageLayoutContentType" : i == 1 ? "ContentPageContentType" : "ContentTypeUnavailable")}
                    WHERE ScanId = {scan} AND RecordKey = {"page:" + state}");
                await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ClassicPages
                    SET LayoutUrl = {web + "/_catalogs/masterpage/old.aspx"}, LayoutReferenceStatus = {referenceStates[i]},
                        LayoutReferenceReason = {referenceReasons[i]} WHERE ScanId = {scan} AND PageUrl = {url}");
            }
            db.ClassicPageWebParts.Add(new ClassicPageWebPart { ScanId = scan, SiteUrl = site, WebUrl = web, PageUrl = url,
                WebPartType = "Historical.Type", WebPartTitle = error, WebPartProperties = "{\"Content\":\"Historical body\"}" });
        }
        // Finished results must not be repaired. An independently queued web gives restart real
        // remaining work; this does not assert retention of incomplete-web transient analysis.
        db.SiteCollections.Add(new SiteCollection { ScanId = scan, SiteUrl = site, Status = SiteWebStatus.Queued });
        db.Webs.Add(new Web { ScanId = scan, SiteUrl = site, WebUrl = web, Status = SiteWebStatus.Finished, Template = "STS#0" });
        db.Webs.Add(new Web { ScanId = scan, SiteUrl = site, WebUrl = web + "/pending", Status = SiteWebStatus.Queued, Template = "ENTERWIKI#0" });
        db.ClassicWebSummaries.Add(new ClassicWebSummary { ScanId = scan, SiteUrl = site, WebUrl = web,
            ClassicPages = 4, ClassicPublishingPages = 4, PagesWithWebParts = 4, UnmappedWebPartPages = 4, AvgMappingPercentage = 42 });
        db.ClassicPublishingSiteSummaries.Add(new ClassicPublishingSiteSummary { ScanId = scan, SiteUrl = site,
            NumberOfPages = 4, NumberOfWebs = 1, UsedPageLayouts = "HistoricalLayout", LastPageUpdateDate = new DateTime(2026, 1, 2) });
        db.ClassicWebPartUniques.Add(new ClassicWebPartUnique { ScanId = scan, WebPartType = "Historical.Type", PageCount = 4 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var tables = new[] { "ClassicPageDiscoveries", "ClassicPages", "ClassicPageWebParts", "ClassicWebSummaries",
            "ClassicPublishingSiteSummaries", "ClassicWebPartUniques" };
        var columns = tables.ToDictionary(t => t, t => Columns(connection, t));
        var before = tables.ToDictionary(t => t, t => Snapshot(connection, t, columns[t]));
        var operations = new Migration[] { new ClassicAssetPurpose(), new ClassicPublishingLayoutReference(), new ClassicPublishingLayoutTypeEvidence() }
            .SelectMany(m => m.UpOperations).ToArray();
        operations.Should().HaveCount(16).And.OnlyContain(op => op is AddColumnOperation);
        migrator.GenerateScript(baseline).Should().NotContain("DROP ").And.NotContain("RENAME ").And.NotContain("UPDATE ").And.NotContain("DELETE ");
        await migrator.MigrateAsync();
        AssertUnchanged();

        var storage = new StorageManager(new EphemeralDataProtectionProvider(), new ConfigurationBuilder().Build());
        await storage.ConsolidatedScanToEnableRestartAsync(scan);
        await storage.RestartScanAsync(scan);
        (await StorageManager.WebsToRestartScanningAsync(db, scan, site)).Should().ContainSingle().Which.WebTemplate.Should().Be("ENTERWIKI#0");
        var writer = new AssessmentDiscoveryWriter(scan);
        await writer.FinalizeScanAsync(scan);
        await writer.FinalizeScanAsync(scan);
        var authority = await db.Scans.AsNoTracking().SingleAsync();
        authority.PublishingLayoutRuleVersion.Should().Be(0);
        authority.PublishingLayoutTypeCatalogJson.Should().BeNull();
        PublishingLayoutTypeEvidence.ForScan(authority, (_, _) => throw new InvalidOperationException("Historical scans must not inspect source")).Should().BeNull();
        var inventory = await db.ClassicPageDiscoveries.AsNoTracking().Where(r => r.RowType == "Page").ToListAsync();
        inventory.Should().HaveCount(4).And.OnlyContain(r => r.PublishingLayoutFamily == "Unknown" &&
            r.PageTypeSourceStatus == "Unknown" && r.PageTypeResolutionStatus == "Unknown" && r.PageTypeReason == "NotEvaluated" &&
            r.PageTypeEvidenceOrigin == "None" && r.PageTypeEvidenceJson == null && r.DeclaredPageType == null && r.ResolvedPageType == null);
        if (!hasLayouts) inventory.Should().OnlyContain(r => r.AssetPurpose == "Unknown" && r.AssetPurposeStatus == "Unknown" && r.AssetPurposeReason == "NotEvaluated");
        var directory = Path.Combine(Path.GetTempPath(), "historical-family-export-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            await ReportManager.ExportClassicReportDataAsync(db, scan, directory, new CsvConfiguration(CultureInfo.InvariantCulture));
            var pages = await db.ClassicPages.AsNoTracking().ToListAsync();
            ReadCsv<ClassicPage>("classicpages.csv").Should().BeEquivalentTo(pages);
            ReadCsv<ClassicPageDiscovery>("discovery.csv").Where(r => r.RowType == "Page").Should()
                .BeEquivalentTo(inventory, config => config.Excluding(r => r.PageType));
            ReadCsv<ClassicPageWebPart>("classicpagewebparts.csv").Should().BeEquivalentTo(await db.ClassicPageWebParts.AsNoTracking().ToListAsync());
            ReadCsv<ClassicWebSummary>("classicwebsummaries.csv").Should().BeEquivalentTo(await db.ClassicWebSummaries.AsNoTracking().ToListAsync());
            ReadCsv<ClassicPublishingSiteSummary>("classicpublishingsitesummaries.csv").Should().BeEquivalentTo(await db.ClassicPublishingSiteSummaries.AsNoTracking().ToListAsync());
            ReadCsv<ClassicWebPartUnique>("classicwebpartunique.csv").Should().BeEquivalentTo(await db.ClassicWebPartUniques.AsNoTracking().ToListAsync());
            if (!hasLayouts) pages.Should().OnlyContain(p => p.LayoutUrl == null && p.LayoutReferenceStatus == "Unknown" && p.LayoutReferenceReason == "NotEvaluated");
            else foreach (int i in Enumerable.Range(0, states.Length))
            {
                var page = pages.Single(p => p.AssessmentStatus == states[i]);
                page.LayoutReferenceStatus.Should().Be(referenceStates[i]);
                page.LayoutReferenceReason.Should().Be(referenceReasons[i]);
            }
            AssertUnchanged();
        }
        finally { Directory.Delete(directory, true); }

        void AssertUnchanged()
        {
            foreach (var table in tables)
            {
                Columns(connection, table).Should().Contain(columns[table]);
                // Finalization adds a coverage summary, not a rewrite of a historical page.
                var after = Snapshot(connection, table, columns[table]);
                if (table == "ClassicPageDiscoveries") after = after.Take(4).ToArray();
                after.Should().Equal(before[table], "all original result columns must survive upgrade, restart, finalization and export");
            }
        }

        T[] ReadCsv<T>(string name)
        {
            using var csv = new CsvReader(new StreamReader(Path.Combine(directory, name)), CultureInfo.InvariantCulture);
            csv.Context.TypeConverterOptionsCache.GetOptions<string>().NullValues.Add("");
            return csv.GetRecords<T>().ToArray();
        }
    }
}
