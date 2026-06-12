using FluentAssertions;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage
{
    /// <summary>
    /// T9 — verifies the post-scan summary rollups: <see cref="StorageManager.ComputeAndStoreWebPageRollupsAsync"/>
    /// aggregates each web's per-page transformation readiness into its <see cref="ClassicWebSummary"/>, and
    /// <see cref="StorageManager.PopulateWebPartUniqueAsync"/> builds the scan-wide unique web part inventory.
    /// Both run against the shared in-memory SQLite <see cref="ScanContext"/> fixture.
    /// </summary>
    public class ClassicPageRollupTests : IClassFixture<ScanContextFixture>
    {
        private readonly ScanContextFixture fixture;

        public ClassicPageRollupTests(ScanContextFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task Rollup_SeededPages_MatchesHandComputed()
        {
            var scanId = Guid.NewGuid();
            var siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";

            // Web with pages: three carry web parts (75 / 100 / 50), one carries none (the home page,
            // flagged uncustomized). A second, page-less web must roll up to all zeros.
            var pages = new List<ClassicPage>
            {
                NewPage(scanId, siteUrl, "/", "/Pages/a.aspx", webPartCount: 4, mappingPercentage: 75),
                NewPage(scanId, siteUrl, "/", "/Pages/b.aspx", webPartCount: 2, mappingPercentage: 100),
                NewPage(scanId, siteUrl, "/", "/Pages/c.aspx", webPartCount: 3, mappingPercentage: 50),
                NewPage(scanId, siteUrl, "/", "/SitePages/Home.aspx", webPartCount: 0, mappingPercentage: 100,
                        homePage: true, uncustomizedHomePage: true),
            };

            var webSummaries = new List<ClassicWebSummary>
            {
                NewWebSummary(scanId, siteUrl, "/"),
                NewWebSummary(scanId, siteUrl, "/subweb"),
            };

            using (var context = fixture.CreateContext())
            {
                await context.ClassicPages.AddRangeAsync(pages);
                await context.ClassicWebSummaries.AddRangeAsync(webSummaries);
                await context.SaveChangesAsync();

                await StorageManager.ComputeAndStoreWebPageRollupsAsync(context, scanId);
            }

            using (var context = fixture.CreateContext())
            {
                var root = context.ClassicWebSummaries.Single(w => w.ScanId == scanId && w.WebUrl == "/");

                root.PagesWithWebParts.Should().Be(3);            // a, b, c (Home has no web parts)
                root.MappableWebPartPages.Should().Be(1);         // b (100%)
                root.UnmappedWebPartPages.Should().Be(2);         // a, c (< 100%)
                root.UncustomizedHomePages.Should().Be(1);        // Home
                root.AvgMappingPercentage.Should().BeApproximately(75.0, 0.0001); // (75+100+50)/3

                // Mappable + Unmapped partitions the pages-with-web-parts set.
                (root.MappableWebPartPages + root.UnmappedWebPartPages).Should().Be(root.PagesWithWebParts);

                var subweb = context.ClassicWebSummaries.Single(w => w.ScanId == scanId && w.WebUrl == "/subweb");
                subweb.PagesWithWebParts.Should().Be(0);
                subweb.MappableWebPartPages.Should().Be(0);
                subweb.UnmappedWebPartPages.Should().Be(0);
                subweb.UncustomizedHomePages.Should().Be(0);
                subweb.AvgMappingPercentage.Should().Be(0);
            }
        }

        [Fact]
        public async Task Rollup_WebPartUnique_AggregatesPageCounts()
        {
            var scanId = Guid.NewGuid();
            var siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";

            // ContentEditor appears twice on page A (counts the page once) and once on page B -> 2 pages.
            // A custom (non-mapped) web part appears once on page A -> 1 page.
            var webParts = new List<ClassicPageWebPart>
            {
                NewWebPart(scanId, siteUrl, "/", "/Pages/a.aspx", index: 0, type: "ContentEditorWebPart", isMappable: true),
                NewWebPart(scanId, siteUrl, "/", "/Pages/a.aspx", index: 1, type: "ContentEditorWebPart", isMappable: true),
                NewWebPart(scanId, siteUrl, "/", "/Pages/a.aspx", index: 2, type: "Contoso.Custom.WebPart", isMappable: false),
                NewWebPart(scanId, siteUrl, "/", "/Pages/b.aspx", index: 0, type: "ContentEditorWebPart", isMappable: true),
            };

            using (var context = fixture.CreateContext())
            {
                await context.ClassicPageWebParts.AddRangeAsync(webParts);
                await context.SaveChangesAsync();

                await StorageManager.PopulateWebPartUniqueAsync(context, scanId);
            }

            using (var context = fixture.CreateContext())
            {
                var uniques = context.ClassicWebPartUniques
                    .Where(u => u.ScanId == scanId)
                    .ToList();

                uniques.Should().HaveCount(2);

                var contentEditor = uniques.Single(u => u.WebPartType == "ContentEditorWebPart");
                contentEditor.InMappingFile.Should().BeTrue();
                contentEditor.PageCount.Should().Be(2);

                var custom = uniques.Single(u => u.WebPartType == "Contoso.Custom.WebPart");
                custom.InMappingFile.Should().BeFalse();
                custom.PageCount.Should().Be(1);
            }
        }

        private static ClassicPage NewPage(Guid scanId, string siteUrl, string webUrl, string pageUrl,
                                           int webPartCount, double mappingPercentage,
                                           bool homePage = false, bool uncustomizedHomePage = false)
        {
            return new ClassicPage
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = webUrl,
                PageUrl = pageUrl,
                PageName = Path.GetFileName(pageUrl),
                PageType = "WikiPage",
                WebPartCount = webPartCount,
                MappingPercentage = mappingPercentage,
                HomePage = homePage,
                UncustomizedHomePage = uncustomizedHomePage,
            };
        }

        private static ClassicWebSummary NewWebSummary(Guid scanId, string siteUrl, string webUrl)
        {
            return new ClassicWebSummary
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = webUrl,
                Template = "STS#0",
            };
        }

        private static ClassicPageWebPart NewWebPart(Guid scanId, string siteUrl, string webUrl, string pageUrl,
                                                     int index, string type, bool isMappable)
        {
            return new ClassicPageWebPart
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = webUrl,
                PageUrl = pageUrl,
                WebPartIndex = index,
                WebPartType = type,
                WebPartTypeShort = type.Split('.').Last(),
                IsMappable = isMappable,
            };
        }
    }
}
