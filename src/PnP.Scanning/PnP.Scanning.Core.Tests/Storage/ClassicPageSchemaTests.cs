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

        [Fact]
        public void Schema_ClassicPageAuditUsage_CanPersistAndRetrieve()
        {
            var scanId  = Guid.NewGuid();
            var siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";
            var pageUrl = $"{siteUrl}/SitePages/Home.aspx";
            var windowStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var windowEnd   = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

            using (var context = fixture.CreateContext())
            {
                context.ClassicPageAuditUsages.Add(new ClassicPageAuditUsage
                {
                    ScanId = scanId, SiteUrl = siteUrl, WebUrl = "/",
                    PageUrl = pageUrl,
                    AuditViewsCount = 5, AuditCreatesCount = 1, AuditEditsCount = 2, AuditUniqueUsers = 3,
                    AuditWindowStart = windowStart, AuditWindowEnd = windowEnd,
                    QueryStatus = "succeeded", SkipReason = null,
                });
                context.SaveChanges();
            }

            using (var context = fixture.CreateContext())
            {
                var row = context.ClassicPageAuditUsages.Single(r => r.ScanId == scanId && r.PageUrl == pageUrl);
                row.AuditViewsCount.Should().Be(5);
                row.AuditCreatesCount.Should().Be(1);
                row.AuditEditsCount.Should().Be(2);
                row.AuditUniqueUsers.Should().Be(3);
                row.AuditWindowStart.Should().Be(windowStart);
                row.AuditWindowEnd.Should().Be(windowEnd);
                row.QueryStatus.Should().Be("succeeded");
                row.SkipReason.Should().BeNull();
            }
        }

        [Fact]
        public void Schema_ClassicPageAuditUsage_DuplicateCompositeKey_Throws()
        {
            var scanId  = Guid.NewGuid();
            var siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";

            ClassicPageAuditUsage Row() => new()
            {
                ScanId = scanId, SiteUrl = siteUrl, WebUrl = "/",
                PageUrl = $"{siteUrl}/SitePages/Home.aspx",
                AuditWindowStart = DateTime.UtcNow, AuditWindowEnd = DateTime.UtcNow,
                QueryStatus = "succeeded",
            };

            using (var context = fixture.CreateContext())
            {
                context.ClassicPageAuditUsages.Add(Row());
                context.SaveChanges();
            }

            using (var context = fixture.CreateContext())
            {
                context.ClassicPageAuditUsages.Add(Row());
                Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
            }
        }
    }
}
