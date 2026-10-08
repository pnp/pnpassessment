using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage
{
    public class PageHandlerStorageTests : IClassFixture<ScanContextFixture>
    {
        private readonly ScanContextFixture fixture;

        public PageHandlerStorageTests(ScanContextFixture fixture) => this.fixture = fixture;

        [Fact]
        public async Task Legacy_database_upgrade_keeps_rows_and_leaves_handler_evidence_empty()
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options;
            using var context = new ScanContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260918022217_ClassicAspxDiscovery");
            Guid scan = Guid.NewGuid();
            string site = "https://contoso.sharepoint.com/sites/legacy";
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ClassicPages
                    (ScanId, SiteUrl, WebUrl, PageUrl, PageType, ListId, ModifiedAt,
                     UncustomizedHomePage, WebPartCount, MappingPercentage)
                VALUES ({scan}, {site}, '/', '/SitePages/Home.aspx', 'WikiPage', {Guid.Empty},
                        {DateTime.UtcNow}, 0, 0, 0)
                """);
            await migrator.MigrateAsync();
            var page = await context.ClassicPages.SingleAsync();
            page.PageType.Should().Be("WikiPage");
            page.PageHandler.Should().BeNull();
            page.PageHandlerEvidenceJson.Should().BeNull();
        }

        [Theory]
        [InlineData(",")]
        [InlineData(";")]
        public async Task Persisted_handler_values_round_trip_as_the_only_new_csv_column(string delimiter)
        {
            Guid scan = Guid.NewGuid();
            string site = $"https://contoso.sharepoint.com/sites/{scan:N}";
            var declared = Page(scan, site, "/SitePages/Declared.aspx");
            declared.PageHandler = "Acme.Generic\u00601[[Acme.中文Page, Acme.Pages]]";
            declared.PageHandlerEvidenceJson = "{\"DeclaredInherits\":\"evidence-stays-in-sqlite\"}";
            var failed = Page(scan, site, "/SitePages/Denied.aspx");
            failed.PageHandler = "ERROR: ReadFailed (HTTP 403)";
            failed.PageHandlerEvidenceJson = "{\"ErrorDetail\":\"detail-stays-in-sqlite\"}";
            using (var context = fixture.CreateContext())
                await StorageManager.StorePageInformationAsync(context, new() { declared, failed });

            string export = Path.Combine(Path.GetTempPath(), "pnp-handler-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(export);
            try
            {
                using (var context = fixture.CreateContext())
                {
                    var stored = await context.ClassicPages.SingleAsync(p => p.ScanId == scan && p.PageUrl == declared.PageUrl);
                    stored.PageHandlerEvidenceJson.Should().Be(declared.PageHandlerEvidenceJson);
                    await ReportManager.ExportClassicReportDataAsync(context, scan, export,
                        new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = delimiter });
                }
                string file = Path.Combine(export, "classicpages.csv");
                using var reader = new StreamReader(file);
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = delimiter });
                await csv.ReadAsync();
                csv.ReadHeader();
                csv.HeaderRecord.Should().Equal(new[]
                {
                    "PageUrl", "PageName", "PageType", "ListUrl", "ListTitle", "ListId",
                    "SiteCollectionId", "WebId", "FileUniqueId", "ListItemId", "DiscoveryStatus", "AssessmentStatus",
                    "ModifiedAt", "Layout", "HomePage", "UncustomizedHomePage", "ModifiedBy",
                    "WebPartCount", "MappingPercentage", "UnmappedWebParts", "RemediationCode",
                    "ScanId", "SiteUrl", "WebUrl", "PageHandler",
                });
                var handlers = new Dictionary<string, string>();
                while (await csv.ReadAsync())
                    handlers.Add(csv.GetField("PageUrl"), csv.GetField("PageHandler"));
                handlers.Should().HaveCount(2);
                handlers[declared.PageUrl].Should().Be(declared.PageHandler);
                handlers[failed.PageUrl].Should().Be(failed.PageHandler);
                string text = await File.ReadAllTextAsync(file);
                text.Should().NotContain("PageHandlerEvidenceJson");
                text.Should().NotContain("stays-in-sqlite");
            }
            finally
            {
                Directory.Delete(export, recursive: true);
            }
        }

        private static ClassicPage Page(Guid scan, string site, string url) => new()
        {
            ScanId = scan, SiteUrl = site, WebUrl = "/", PageUrl = url,
            PageName = Path.GetFileName(url), PageType = PageScanComponent.WikiPage,
            ModifiedAt = DateTime.UtcNow, AssessmentStatus = "Complete",
        };
    }
}
