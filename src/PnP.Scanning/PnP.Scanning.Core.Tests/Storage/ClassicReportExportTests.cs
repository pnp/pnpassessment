using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using FluentAssertions;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage
{
    /// <summary>
    /// T11 — verifies the Classic CSV report export: the new <c>classicpagewebparts.csv</c> /
    /// <c>classicwebpartunique.csv</c> files and the enriched <c>classicpages.csv</c> columns are
    /// emitted with the right header schema and row values. Seeds the shared in-memory SQLite
    /// <see cref="ScanContext"/> fixture, runs the testable static export core
    /// <see cref="ReportManager.ExportClassicReportDataAsync"/> into a temp directory, then re-reads
    /// each CSV with CsvHelper.
    /// </summary>
    public class ClassicReportExportTests : IClassFixture<ScanContextFixture>
    {
        private readonly ScanContextFixture fixture;

        public ClassicReportExportTests(ScanContextFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task Csv_PageWebParts_HeaderAndRows()
        {
            var scanId = Guid.NewGuid();
            var siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";
            var pageUrl = "/SitePages/Home.aspx";

            var webParts = new List<ClassicPageWebPart>
            {
                NewWebPart(scanId, siteUrl, pageUrl, index: 0, type: "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart",
                           shortType: "ContentEditorWebPart", isMappable: true),
                NewWebPart(scanId, siteUrl, pageUrl, index: 1, type: "Contoso.Custom.WebPart",
                           shortType: "WebPart", isMappable: false),
            };

            using (var context = fixture.CreateContext())
            {
                await context.ClassicPageWebParts.AddRangeAsync(webParts);
                await context.SaveChangesAsync();
            }

            var exportDir = NewTempDir();
            try
            {
                await ExportAsync(scanId, exportDir);

                var file = Path.Join(exportDir, "classicpagewebparts.csv");
                File.Exists(file).Should().BeTrue();

                ReadHeader(file).Should().Contain(new[]
                {
                    nameof(ClassicPageWebPart.ScanId),
                    nameof(ClassicPageWebPart.SiteUrl),
                    nameof(ClassicPageWebPart.WebUrl),
                    nameof(ClassicPageWebPart.PageUrl),
                    nameof(ClassicPageWebPart.WebPartIndex),
                    nameof(ClassicPageWebPart.WebPartType),
                    nameof(ClassicPageWebPart.WebPartTypeShort),
                    nameof(ClassicPageWebPart.WebPartTitle),
                    nameof(ClassicPageWebPart.ZoneId),
                    nameof(ClassicPageWebPart.Row),
                    nameof(ClassicPageWebPart.Column),
                    nameof(ClassicPageWebPart.Order),
                    nameof(ClassicPageWebPart.Hidden),
                    nameof(ClassicPageWebPart.IsClosed),
                    nameof(ClassicPageWebPart.IsMappable),
                });

                var rows = ReadRecords<ClassicPageWebPart>(file)
                    .Where(wp => wp.ScanId == scanId)
                    .OrderBy(wp => wp.WebPartIndex)
                    .ToList();

                rows.Should().HaveCount(2);

                rows[0].WebPartType.Should().Be("Microsoft.SharePoint.WebPartPages.ContentEditorWebPart");
                rows[0].WebPartTypeShort.Should().Be("ContentEditorWebPart");
                rows[0].ZoneId.Should().Be("Header");
                rows[0].Row.Should().Be(1);
                rows[0].Column.Should().Be(2);
                rows[0].IsMappable.Should().BeTrue();

                rows[1].WebPartType.Should().Be("Contoso.Custom.WebPart");
                rows[1].IsMappable.Should().BeFalse();
            }
            finally
            {
                DeleteTempDir(exportDir);
            }
        }

        [Fact]
        public async Task Csv_WebPartUnique_HeaderAndRows()
        {
            var scanId = Guid.NewGuid();

            var uniques = new List<ClassicWebPartUnique>
            {
                new() { ScanId = scanId, WebPartType = "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart", InMappingFile = true, PageCount = 3 },
                new() { ScanId = scanId, WebPartType = "Contoso.Custom.WebPart", InMappingFile = false, PageCount = 1 },
            };

            using (var context = fixture.CreateContext())
            {
                await context.ClassicWebPartUniques.AddRangeAsync(uniques);
                await context.SaveChangesAsync();
            }

            var exportDir = NewTempDir();
            try
            {
                await ExportAsync(scanId, exportDir);

                var file = Path.Join(exportDir, "classicwebpartunique.csv");
                File.Exists(file).Should().BeTrue();

                ReadHeader(file).Should().Contain(new[]
                {
                    nameof(ClassicWebPartUnique.ScanId),
                    nameof(ClassicWebPartUnique.WebPartType),
                    nameof(ClassicWebPartUnique.InMappingFile),
                    nameof(ClassicWebPartUnique.PageCount),
                });

                var rows = ReadRecords<ClassicWebPartUnique>(file)
                    .Where(u => u.ScanId == scanId)
                    .ToList();

                rows.Should().HaveCount(2);

                var contentEditor = rows.Single(u => u.WebPartType == "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart");
                contentEditor.InMappingFile.Should().BeTrue();
                contentEditor.PageCount.Should().Be(3);

                var custom = rows.Single(u => u.WebPartType == "Contoso.Custom.WebPart");
                custom.InMappingFile.Should().BeFalse();
                custom.PageCount.Should().Be(1);
            }
            finally
            {
                DeleteTempDir(exportDir);
            }
        }

        [Fact]
        public async Task Csv_ClassicPages_HasEnrichedColumns()
        {
            var scanId = Guid.NewGuid();
            var siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";
            var pageUrl = "/SitePages/Home.aspx";

            var page = new ClassicPage
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = siteUrl,
                PageUrl = pageUrl,
                PageName = "Home.aspx",
                PageType = "WikiPage",
                RemediationCode = "CP2",
                Layout = "TwoColumns",
                HomePage = true,
                UncustomizedHomePage = true,
                ModifiedBy = "jane@contoso.com",
                WebPartCount = 4,
                MappingPercentage = 75,
                UnmappedWebParts = "ContosoCustomWebPart",
            };

            using (var context = fixture.CreateContext())
            {
                await context.ClassicPages.AddAsync(page);
                await context.SaveChangesAsync();
            }

            var exportDir = NewTempDir();
            try
            {
                await ExportAsync(scanId, exportDir);

                var file = Path.Join(exportDir, "classicpages.csv");
                File.Exists(file).Should().BeTrue();

                // The enrichment columns the page scan adds must all surface in the page CSV.
                ReadHeader(file).Should().Contain(new[]
                {
                    nameof(ClassicPage.Layout),
                    nameof(ClassicPage.HomePage),
                    nameof(ClassicPage.UncustomizedHomePage),
                    nameof(ClassicPage.ModifiedBy),
                    nameof(ClassicPage.WebPartCount),
                    nameof(ClassicPage.MappingPercentage),
                    nameof(ClassicPage.UnmappedWebParts),
                });

                var row = ReadRecords<ClassicPage>(file).Single(p => p.ScanId == scanId && p.PageUrl == pageUrl);

                row.Layout.Should().Be("TwoColumns");
                row.HomePage.Should().BeTrue();
                row.UncustomizedHomePage.Should().BeTrue();
                row.ModifiedBy.Should().Be("jane@contoso.com");
                row.WebPartCount.Should().Be(4);
                row.MappingPercentage.Should().Be(75);
                row.UnmappedWebParts.Should().Be("ContosoCustomWebPart");
            }
            finally
            {
                DeleteTempDir(exportDir);
            }
        }

        private async Task ExportAsync(Guid scanId, string exportDir)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "," };
            using var context = fixture.CreateContext();
            await ReportManager.ExportClassicReportDataAsync(context, scanId, exportDir, config);
        }

        private static string[] ReadHeader(string file)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "," };
            using var reader = new StreamReader(file);
            using var csv = new CsvReader(reader, config);
            csv.Read();
            csv.ReadHeader();
            return csv.HeaderRecord;
        }

        private static List<T> ReadRecords<T>(string file)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "," };
            using var reader = new StreamReader(file);
            using var csv = new CsvReader(reader, config);
            return csv.GetRecords<T>().ToList();
        }

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pnp-t11-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteTempDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup; a leaked temp dir must not fail the test.
            }
        }

        private static ClassicPageWebPart NewWebPart(Guid scanId, string siteUrl, string pageUrl, int index,
                                                     string type, string shortType, bool isMappable)
        {
            return new ClassicPageWebPart
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = siteUrl,
                PageUrl = pageUrl,
                WebPartIndex = index,
                WebPartType = type,
                WebPartTypeShort = shortType,
                WebPartTitle = $"Web part {index}",
                ZoneId = "Header",
                Row = 1,
                Column = 2,
                Order = index,
                Hidden = false,
                IsClosed = false,
                IsMappable = isMappable,
            };
        }
    }
}
