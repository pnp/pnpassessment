using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage
{
    /// <summary>
    /// Verifies <see cref="StorageManager.PopulatePublishingSiteSummaryAsync"/> — the per-site-collection
    /// publishing-portal rollup that ports the legacy <c>ModernizationPublishingSiteScanResults.csv</c>.
    /// Runs against the shared in-memory SQLite <see cref="ScanContext"/> fixture.
    /// </summary>
    public class ClassicPublishingSiteSummaryTests : IClassFixture<ScanContextFixture>
    {
        private readonly ScanContextFixture fixture;

        public ClassicPublishingSiteSummaryTests(ScanContextFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task PublishingRollup_SeededPortal_MatchesHandComputed()
        {
            var scanId = Guid.NewGuid();
            var portal = $"https://contoso.sharepoint.com/sites/portal-{scanId:N}";
            var team = $"https://contoso.sharepoint.com/sites/team-{scanId:N}";

            // Portal site collection: a publishing root (publishing template) + a subweb that carries
            // publishing pages on a non-publishing template (publishing feature enabled). A second, plain
            // team site has no publishing webs and must NOT produce a row.
            var webSummaries = new List<ClassicWebSummary>
            {
                NewWebSummary(scanId, portal, "/", "BLANKINTERNETCONTAINER#0", isPublishing: true, publishingPages: 2),
                NewWebSummary(scanId, portal, "/sub", "STS#0", isPublishing: false, publishingPages: 1),
                NewWebSummary(scanId, team, "/", "STS#0", isPublishing: false, publishingPages: 0),
            };

            var extensibilities = new List<ClassicExtensibility>
            {
                // Root: a custom site master + a custom system master.
                NewExtensibility(scanId, portal, "/", masterPage: "/_catalogs/masterpage/seattle.master",
                                 customMasterPage: "/_catalogs/masterpage/custom.master"),
                // Subweb: same system master (must dedupe), no custom master.
                NewExtensibility(scanId, portal, "/sub", masterPage: "/_catalogs/masterpage/seattle.master",
                                 customMasterPage: null),
                // Team site master data must be ignored (no publishing web there).
                NewExtensibility(scanId, team, "/", masterPage: "/_catalogs/masterpage/oslo.master",
                                 customMasterPage: "/_catalogs/masterpage/teamcustom.master"),
            };

            var pages = new List<ClassicPage>
            {
                NewPublishingPage(scanId, portal, "/", "/Pages/a.aspx", "ArticleLeft", new DateTime(2026, 1, 1)),
                NewPublishingPage(scanId, portal, "/", "/Pages/b.aspx", "ArticleRight", new DateTime(2026, 3, 15)), // latest
                NewPublishingPage(scanId, portal, "/sub", "/sub/Pages/c.aspx", "ArticleLeft", new DateTime(2026, 2, 1)), // dup layout
                // A non-publishing page on the portal root must be ignored by the publishing rollup.
                NewNonPublishingPage(scanId, portal, "/", "/SitePages/Home.aspx", "OneColumn", new DateTime(2026, 5, 1)),
                // A page on the team site (non-publishing) must be ignored.
                NewNonPublishingPage(scanId, team, "/", "/SitePages/Home.aspx", "OneColumn", new DateTime(2026, 4, 1)),
            };

            using (var context = fixture.CreateContext())
            {
                await context.ClassicWebSummaries.AddRangeAsync(webSummaries);
                await context.ClassicExtensibilities.AddRangeAsync(extensibilities);
                await context.ClassicPages.AddRangeAsync(pages);
                await context.SaveChangesAsync();

                await StorageManager.PopulatePublishingSiteSummaryAsync(context, scanId);
            }

            using (var context = fixture.CreateContext())
            {
                var rows = context.ClassicPublishingSiteSummaries.Where(p => p.ScanId == scanId).ToList();

                // Only the portal site collection is a publishing portal.
                rows.Should().HaveCount(1);

                var portalRow = rows.Single();
                portalRow.SiteUrl.Should().Be(portal);
                portalRow.NumberOfWebs.Should().Be(2);                 // root + sub
                portalRow.NumberOfPages.Should().Be(3);                // 2 + 1
                portalRow.UsedSiteMasterPages.Should().Be("/_catalogs/masterpage/custom.master");
                portalRow.UsedSystemMasterPages.Should().Be("/_catalogs/masterpage/seattle.master"); // deduped across webs
                portalRow.UsedPageLayouts.Should().Be("ArticleLeft,ArticleRight");                    // deduped, sorted
                portalRow.LastPageUpdateDate.Should().Be(new DateTime(2026, 3, 15));                  // max over publishing pages

                // The plain team site produced no publishing-portal row.
                rows.Should().NotContain(r => r.SiteUrl == team);
            }
        }

        [Fact]
        public async Task PublishingRollup_NoPublishingWebs_ProducesNoRows()
        {
            var scanId = Guid.NewGuid();
            var team = $"https://contoso.sharepoint.com/sites/team-{scanId:N}";

            using (var context = fixture.CreateContext())
            {
                await context.ClassicWebSummaries.AddAsync(
                    NewWebSummary(scanId, team, "/", "STS#0", isPublishing: false, publishingPages: 0));
                await context.SaveChangesAsync();

                // Must not throw and must not create any rows.
                await StorageManager.PopulatePublishingSiteSummaryAsync(context, scanId);
            }

            using (var context = fixture.CreateContext())
            {
                context.ClassicPublishingSiteSummaries.Where(p => p.ScanId == scanId).Should().BeEmpty();
            }
        }

        private static ClassicWebSummary NewWebSummary(Guid scanId, string siteUrl, string webUrl, string template,
                                                       bool isPublishing, int publishingPages)
        {
            return new ClassicWebSummary
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = webUrl,
                Template = template,
                IsClassicPublishingSite = isPublishing,
                ClassicPublishingPages = publishingPages,
            };
        }

        private static ClassicExtensibility NewExtensibility(Guid scanId, string siteUrl, string webUrl,
                                                             string masterPage, string customMasterPage)
        {
            return new ClassicExtensibility
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = webUrl,
                MasterPage = masterPage,
                CustomMasterPage = customMasterPage,
            };
        }

        private static ClassicPage NewPublishingPage(Guid scanId, string siteUrl, string webUrl, string pageUrl,
                                                     string layout, DateTime modifiedAt)
        {
            return new ClassicPage
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = webUrl,
                PageUrl = pageUrl,
                PageName = Path.GetFileName(pageUrl),
                PageType = PageScanComponent.PublishingPage,
                Layout = layout,
                ModifiedAt = modifiedAt,
            };
        }

        private static ClassicPage NewNonPublishingPage(Guid scanId, string siteUrl, string webUrl, string pageUrl,
                                                        string layout, DateTime modifiedAt)
        {
            return new ClassicPage
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = webUrl,
                PageUrl = pageUrl,
                PageName = Path.GetFileName(pageUrl),
                PageType = PageScanComponent.WikiPage,
                Layout = layout,
                ModifiedAt = modifiedAt,
            };
        }
    }
}
