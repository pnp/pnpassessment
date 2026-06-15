using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Services
{
    /// <summary>
    /// T14 — verifies the classic page-scan telemetry payload built by
    /// <see cref="TelemetryManager.BuildClassicMetrics"/> (page type distribution + page
    /// transformation readiness). Payload construction only — no Application Insights / network.
    /// Each test owns a fresh in-memory <see cref="ScanContext"/>, mirroring the one-database-per-scan
    /// production layout the metric builder iterates over (it is not scan-id filtered).
    /// </summary>
    public class TelemetryManagerTests
    {
        [Fact]
        public void Telemetry_LogPageScan_BuildsExpectedPayload()
        {
            using var fixture = new ScanContextFixture();
            var scanId = Guid.NewGuid();
            var siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";

            // One page per classic type; three carry web parts (75 / 100 / 50), three carry none.
            // The ASPX page is the (uncustomized) home page.
            var pages = new List<ClassicPage>
            {
                NewPage(scanId, siteUrl, "/Pages/a.aspx", PageScanComponent.WikiPage, webPartCount: 4, mappingPercentage: 75),
                NewPage(scanId, siteUrl, "/Pages/b.aspx", PageScanComponent.WebPartPage, webPartCount: 2, mappingPercentage: 100),
                NewPage(scanId, siteUrl, "/Pages/c.aspx", PageScanComponent.PublishingPage, webPartCount: 3, mappingPercentage: 50),
                NewPage(scanId, siteUrl, "/Lists/Posts/d.aspx", PageScanComponent.BlogPage, webPartCount: 0, mappingPercentage: 100),
                NewPage(scanId, siteUrl, "/default.aspx", PageScanComponent.ASPXPage, webPartCount: 0, mappingPercentage: 100,
                        homePage: true, uncustomizedHomePage: true),
                NewPage(scanId, siteUrl, "/Lists/Posts/f.aspx", PageScanComponent.DelveBlogPage, webPartCount: 0, mappingPercentage: 100),
            };

            // Five web part rows total across the three pages that carry parts.
            var webParts = new List<ClassicPageWebPart>
            {
                NewWebPart(scanId, siteUrl, "/Pages/a.aspx", 0, "ContentEditorWebPart"),
                NewWebPart(scanId, siteUrl, "/Pages/a.aspx", 1, "ContentEditorWebPart"),
                NewWebPart(scanId, siteUrl, "/Pages/b.aspx", 0, "XsltListViewWebPart"),
                NewWebPart(scanId, siteUrl, "/Pages/c.aspx", 0, "Contoso.Custom.WebPart"),
                NewWebPart(scanId, siteUrl, "/Pages/c.aspx", 1, "Contoso.Custom.WebPart"),
            };

            // Scan-wide unique inventory: 2 mapped types, 1 unmapped type.
            var uniques = new List<ClassicWebPartUnique>
            {
                new() { ScanId = scanId, WebPartType = "ContentEditorWebPart", InMappingFile = true, PageCount = 1 },
                new() { ScanId = scanId, WebPartType = "XsltListViewWebPart", InMappingFile = true, PageCount = 1 },
                new() { ScanId = scanId, WebPartType = "Contoso.Custom.WebPart", InMappingFile = false, PageCount = 1 },
            };

            using (var context = fixture.CreateContext())
            {
                context.ClassicPages.AddRange(pages);
                context.ClassicPageWebParts.AddRange(webParts);
                context.ClassicWebPartUniques.AddRange(uniques);
                context.SaveChanges();
            }

            var metric = new Dictionary<string, double>();
            using (var context = fixture.CreateContext())
            {
                TelemetryManager.BuildClassicMetrics(context, metric);
            }

            // Page type distribution
            metric["ClassicPageCount"].Should().Be(6);
            metric["ClassicPageWikiCount"].Should().Be(1);
            metric["ClassicPageWebPartPageCount"].Should().Be(1);
            metric["ClassicPageASPXCount"].Should().Be(1);
            metric["ClassicPagePublishingCount"].Should().Be(1);
            metric["ClassicPageBlogCount"].Should().Be(1);
            metric["ClassicPageDelveBlogCount"].Should().Be(1);

            // Home pages
            metric["ClassicPageHomePageCount"].Should().Be(1);
            metric["ClassicPageUncustomizedHomePageCount"].Should().Be(1);

            // Readiness — only the three pages with web parts contribute
            metric["ClassicPagePagesWithWebPartsCount"].Should().Be(3);
            metric["ClassicPageMappableWebPartPagesCount"].Should().Be(1);   // b (100%)
            metric["ClassicPageUnmappedWebPartPagesCount"].Should().Be(2);   // a (75), c (50)
            metric["ClassicPageAvgMappingPercentage"].Should().BeApproximately(75.0, 0.0001); // (75+100+50)/3

            // Web part inventory
            metric["ClassicPageWebPartCount"].Should().Be(5);
            metric["ClassicPageUniqueWebPartTypeCount"].Should().Be(3);
            metric["ClassicPageMappableWebPartTypeCount"].Should().Be(2);
            metric["ClassicPageUnmappedWebPartTypeCount"].Should().Be(1);
        }

        [Fact]
        public void Telemetry_LogPageScan_EmptyScan_AllZero()
        {
            using var fixture = new ScanContextFixture();

            var metric = new Dictionary<string, double>();
            using (var context = fixture.CreateContext())
            {
                TelemetryManager.BuildClassicMetrics(context, metric);
            }

            // Every metric key is emitted (so the schema is stable) and every value is zero —
            // including the average, which divides-by-zero-safe to 0 when no page carries web parts.
            metric.Values.Should().OnlyContain(v => v == 0);
            metric.Should().ContainKey("ClassicPageCount");
            metric.Should().ContainKey("ClassicPageAvgMappingPercentage");
            metric["ClassicPageAvgMappingPercentage"].Should().Be(0);
        }

        private static ClassicPage NewPage(Guid scanId, string siteUrl, string pageUrl, string pageType,
                                           int webPartCount, double mappingPercentage,
                                           bool homePage = false, bool uncustomizedHomePage = false)
        {
            return new ClassicPage
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = "/",
                PageUrl = pageUrl,
                PageName = Path.GetFileName(pageUrl),
                PageType = pageType,
                WebPartCount = webPartCount,
                MappingPercentage = mappingPercentage,
                HomePage = homePage,
                UncustomizedHomePage = uncustomizedHomePage,
            };
        }

        private static ClassicPageWebPart NewWebPart(Guid scanId, string siteUrl, string pageUrl, int index, string type)
        {
            return new ClassicPageWebPart
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = "/",
                PageUrl = pageUrl,
                WebPartIndex = index,
                WebPartType = type,
                WebPartTypeShort = type.Split('.').Last(),
            };
        }
    }
}
