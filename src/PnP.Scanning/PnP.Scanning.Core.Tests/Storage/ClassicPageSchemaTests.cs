using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage
{
    /// <summary>
    /// T1 — verifies the v1.12.0 page-scan enrichment schema: the new ClassicPage columns,
    /// the ClassicPageWebPart / ClassicWebPartUnique tables, and their composite keys.
    /// </summary>
    public class ClassicPageSchemaTests : IClassFixture<ScanContextFixture>
    {
        private readonly ScanContextFixture fixture;

        public ClassicPageSchemaTests(ScanContextFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public void Schema_Migration_CreatesNewTablesAndColumns()
        {
            var scanId = Guid.NewGuid();
            var siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";
            var webUrl = siteUrl;
            var pageUrl = "SitePages/Home.aspx";

            // --- Write: enriched ClassicPage + child web part + unique inventory row ---
            using (var context = fixture.CreateContext())
            {
                context.ClassicPages.Add(new ClassicPage
                {
                    ScanId = scanId,
                    SiteUrl = siteUrl,
                    WebUrl = webUrl,
                    PageUrl = pageUrl,
                    PageName = "Home.aspx",
                    PageType = "WikiPage",
                    Layout = "Wiki_OneColumn",
                    HomePage = true,
                    UncustomizedHomePage = true,
                    ModifiedBy = "Jane Doe",
                    ViewsRecent = 10,
                    ViewsRecentUniqueUsers = 4,
                    ViewsLifeTime = 100,
                    ViewsLifeTimeUniqueUsers = 25,
                    WebPartCount = 2,
                    MappingPercentage = 50.0,
                    UnmappedWebParts = "CustomWebPart",
                });

                context.ClassicPageWebParts.Add(new ClassicPageWebPart
                {
                    ScanId = scanId,
                    SiteUrl = siteUrl,
                    WebUrl = webUrl,
                    PageUrl = pageUrl,
                    WebPartIndex = 0,
                    WebPartType = "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart",
                    WebPartTypeShort = "ContentEditorWebPart",
                    WebPartTitle = "Content Editor",
                    WebPartProperties = "{\"Title\":\"Content Editor\"}",
                    ZoneId = "Header",
                    Row = 1,
                    Column = 1,
                    Order = 0,
                    Hidden = false,
                    IsClosed = false,
                    IsMappable = true,
                });

                context.ClassicWebPartUniques.Add(new ClassicWebPartUnique
                {
                    ScanId = scanId,
                    WebPartType = "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart",
                    InMappingFile = true,
                    PageCount = 1,
                });

                context.SaveChanges();
            }

            // --- Read back: new ClassicPage columns persisted ---
            using (var context = fixture.CreateContext())
            {
                var page = context.ClassicPages.Single(p =>
                    p.ScanId == scanId && p.SiteUrl == siteUrl && p.WebUrl == webUrl && p.PageUrl == pageUrl);

                page.Layout.Should().Be("Wiki_OneColumn");
                page.HomePage.Should().BeTrue();
                page.UncustomizedHomePage.Should().BeTrue();
                page.ModifiedBy.Should().Be("Jane Doe");
                page.ViewsRecent.Should().Be(10);
                page.ViewsRecentUniqueUsers.Should().Be(4);
                page.ViewsLifeTime.Should().Be(100);
                page.ViewsLifeTimeUniqueUsers.Should().Be(25);
                page.WebPartCount.Should().Be(2);
                page.MappingPercentage.Should().Be(50.0);
                page.UnmappedWebParts.Should().Be("CustomWebPart");

                var webPart = context.ClassicPageWebParts.Single(wp =>
                    wp.ScanId == scanId && wp.PageUrl == pageUrl && wp.WebPartIndex == 0);
                webPart.WebPartTypeShort.Should().Be("ContentEditorWebPart");
                webPart.ZoneId.Should().Be("Header");
                webPart.Row.Should().Be(1);
                webPart.Column.Should().Be(1);
                webPart.IsMappable.Should().BeTrue();

                var unique = context.ClassicWebPartUniques.Single(u =>
                    u.ScanId == scanId && u.WebPartType == "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart");
                unique.InMappingFile.Should().BeTrue();
                unique.PageCount.Should().Be(1);
            }
        }

        [Fact]
        public void Schema_ClassicPageWebPart_DuplicateCompositeKey_Throws()
        {
            var scanId = Guid.NewGuid();
            var siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";

            ClassicPageWebPart Row() => new()
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = siteUrl,
                PageUrl = "SitePages/Home.aspx",
                WebPartIndex = 0,
                WebPartType = "Some.WebPart",
            };

            using (var context = fixture.CreateContext())
            {
                context.ClassicPageWebParts.Add(Row());
                context.SaveChanges();
            }

            using (var context = fixture.CreateContext())
            {
                context.ClassicPageWebParts.Add(Row());
                // Same (ScanId, SiteUrl, WebUrl, PageUrl, WebPartIndex) → PK violation.
                Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
            }
        }

        [Fact]
        public void Schema_ClassicWebPartUnique_DuplicateCompositeKey_Throws()
        {
            var scanId = Guid.NewGuid();

            ClassicWebPartUnique Row() => new()
            {
                ScanId = scanId,
                WebPartType = "Some.WebPart",
                InMappingFile = true,
                PageCount = 1,
            };

            using (var context = fixture.CreateContext())
            {
                context.ClassicWebPartUniques.Add(Row());
                context.SaveChanges();
            }

            using (var context = fixture.CreateContext())
            {
                context.ClassicWebPartUniques.Add(Row());
                // Same (ScanId, WebPartType) → PK violation.
                Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
            }
        }
    }
}
