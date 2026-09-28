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
using PnP.Scanning.Core.Storage.DatabaseMigration;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

public sealed class ClassicLayoutUpgradeTests
{
    [Fact]
    public async Task Pre_layout_database_upgrades_and_exports_without_repairing_any_historical_column()
    {
        const string baseline = "20260918022217_ClassicAspxDiscovery";
        const string site = "https://contoso.sharepoint.com/sites/history";
        const string web = "/sites/history";
        var scan = Guid.NewGuid();
        var modified = new DateTime(2026, 1, 2);
        var states = new[] { "Denied", "Failed", "Unknown", "Complete" };
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options;
        using var db = new ScanContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(baseline);
        (await db.Database.GetAppliedMigrationsAsync()).Last().Should().Be(baseline);

        foreach (var state in states)
        {
            // Preserve even a layout that older scans called PublishingPage. Its content type
            // deliberately makes a retrospective classifier/backfill detectable.
            string url = web + (state == "Complete" ? "/Pages/" : "/_catalogs/masterpage/") + state + ".aspx";
            string contentType = state == "Complete" ? AspxAssetPurpose.PublishingContentType : AspxAssetPurpose.LayoutContentType;
            var fileId = Guid.NewGuid();
            string error = "Historical, \"quoted\"\n" + state;
            string discoveryStatus = state == "Complete" ? "Discovered" : state;
            await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ClassicPageDiscoveries
                (ScanId, RecordKey, RowType, DiscoveryStatus, AssessmentStatus, ObservedAtUtc, SiteUrl, WebUrl,
                 Url, FileUniqueId, ListItemId, PageType, ContentTypeId, ErrorDetail, EvidenceJson)
                VALUES ({scan}, {"page:" + state}, 'Page', {discoveryStatus}, {state}, {modified}, {site}, {web},
                    {url}, {fileId}, 1, 'PublishingPage', {contentType}, {error}, '{{""historical"":true}}')");
            await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO ClassicPages
                (ScanId, SiteUrl, WebUrl, PageUrl, ListId, PageType, Layout, DiscoveryStatus, AssessmentStatus,
                 FileUniqueId, ListItemId, ModifiedAt, HomePage, UncustomizedHomePage, WebPartCount, MappingPercentage, UnmappedWebParts)
                VALUES ({scan}, {site}, {web}, {url}, {Guid.Empty}, 'PublishingPage', 'HistoricalLayout', {discoveryStatus},
                    {state}, {fileId}, 1, {modified}, NULL, 0, 7, 42, 'HistoricalUnmapped')");
            // These tables did not change in this checkpoint, so the current model can write
            // their representative old report rows at the pre-checkpoint migration level.
            db.ClassicPageWebParts.Add(new ClassicPageWebPart
            {
                ScanId = scan, SiteUrl = site, WebUrl = web, PageUrl = url,
                WebPartType = "Historical.Type", WebPartTypeShort = "Type", WebPartTitle = error,
                WebPartProperties = "{\"Content\":\"Historical body\"}", IsMappable = false,
            });
        }
        db.ClassicWebSummaries.Add(new ClassicWebSummary
        {
            ScanId = scan, SiteUrl = site, WebUrl = web, ClassicPublishingPages = 4,
            PagesWithWebParts = 4, UnmappedWebPartPages = 4, AvgMappingPercentage = 42,
        });
        db.ClassicPublishingSiteSummaries.Add(new ClassicPublishingSiteSummary
        {
            ScanId = scan, SiteUrl = site, NumberOfPages = 4, NumberOfWebs = 1,
            UsedPageLayouts = "HistoricalLayout", LastPageUpdateDate = modified,
        });
        db.ClassicWebPartUniques.Add(new ClassicWebPartUnique
        {
            ScanId = scan, WebPartType = "Historical.Type", PageCount = 4, InMappingFile = false,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var tables = new[] { "ClassicPageDiscoveries", "ClassicPages", "ClassicPageWebParts",
            "ClassicWebSummaries", "ClassicPublishingSiteSummaries", "ClassicWebPartUniques" };
        var originalColumns = tables.ToDictionary(table => table, table => Columns(connection, table));
        originalColumns["ClassicPages"].Should().NotContain("LayoutUrl");
        originalColumns["ClassicPageDiscoveries"].Should().NotContain("AssetPurpose");
        var originalRows = tables.ToDictionary(table => table, table => Snapshot(connection, table, originalColumns[table]));

        var migrations = new Migration[] { new ClassicAssetPurpose(), new ClassicPublishingLayoutReference() };
        migrations.SelectMany(migration => migration.UpOperations).Should().HaveCount(6)
            .And.OnlyContain(operation => operation is AddColumnOperation,
                "upgrade must not drop, rename, rewrite or repair historical data");
        var upgradeSql = migrator.GenerateScript(baseline);
        upgradeSql.Should().NotContain("DROP ").And.NotContain("RENAME ").And.NotContain("UPDATE ").And.NotContain("DELETE ");
        await migrator.MigrateAsync();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

        using var read = new ScanContext(options);
        var pages = await read.ClassicPages.ToListAsync();
        pages.Should().HaveCount(4).And.OnlyContain(page => page.PageType == "PublishingPage" &&
            page.Layout == "HistoricalLayout" && page.LayoutUrl == null && page.LayoutReferenceStatus == "Unknown" &&
            page.LayoutReferenceReason == "NotEvaluated" && page.WebPartCount == 7 && page.MappingPercentage == 42 &&
            page.UnmappedWebParts == "HistoricalUnmapped" && page.HomePage == null);
        pages.Select(page => page.AssessmentStatus).Should().BeEquivalentTo(states);
        var inventory = await read.ClassicPageDiscoveries.ToListAsync();
        inventory.Should().HaveCount(4).And.OnlyContain(row => row.AssetPurpose == "Unknown" &&
            row.AssetPurposeStatus == "Unknown" && row.AssetPurposeReason == "NotEvaluated" && row.PageType == "PublishingPage");
        inventory.Count(row => row.ContentTypeId == AspxAssetPurpose.LayoutContentType).Should().Be(3);

        var directory = Path.Combine(Path.GetTempPath(), "layout-upgrade-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await ReportManager.ExportClassicReportDataAsync(read, scan, directory, new CsvConfiguration(CultureInfo.InvariantCulture));
            ReadCsv<ClassicPage>("classicpages.csv").Should().BeEquivalentTo(pages);
            var exported = ReadCsv<ClassicPageDiscovery>("discovery.csv");
            exported.Should().BeEquivalentTo(inventory, config => config.Excluding(row => row.PageType));
            ReadCsv<ClassicPageWebPart>("classicpagewebparts.csv").Should().BeEquivalentTo(await read.ClassicPageWebParts.ToListAsync());
            ReadCsv<ClassicWebSummary>("classicwebsummaries.csv").Should().BeEquivalentTo(await read.ClassicWebSummaries.ToListAsync());
            ReadCsv<ClassicPublishingSiteSummary>("classicpublishingsitesummaries.csv").Should().BeEquivalentTo(await read.ClassicPublishingSiteSummaries.ToListAsync());
            ReadCsv<ClassicWebPartUnique>("classicwebpartunique.csv").Should().BeEquivalentTo(await read.ClassicWebPartUniques.ToListAsync());
            foreach (var table in tables)
            {
                Columns(connection, table).Should().Contain(originalColumns[table]);
                Snapshot(connection, table, originalColumns[table]).Should().Equal(originalRows[table],
                    "every original column must remain unchanged after migration, reading and native export");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }

        T[] ReadCsv<T>(string name)
        {
            using var csv = new CsvReader(new StreamReader(Path.Combine(directory, name)), CultureInfo.InvariantCulture);
            // Native CSV represents database null strings as empty fields.
            csv.Context.TypeConverterOptionsCache.GetOptions<string>().NullValues.Add("");
            return csv.GetRecords<T>().ToArray();
        }
    }

    private static string[] Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns.ToArray();
    }

    private static string[] Snapshot(SqliteConnection connection, string table, string[] columns)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(',', columns.Select(column => $"\"{column}\""))} FROM \"{table}\" ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(JsonSerializer.Serialize(values));
        }
        return rows.ToArray();
    }
}
