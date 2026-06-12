using FluentAssertions;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage
{
    /// <summary>
    /// T8 — verifies the page-scan persistence: the new <see cref="StorageManager.StorePageWebPartsAsync"/>
    /// bulk insert and that <see cref="StorageManager.StorePageInformationAsync"/> round-trips the
    /// page-scan enrichment columns. Exercises the testable static cores against the shared in-memory
    /// SQLite <see cref="ScanContext"/> fixture (the scanId overloads open the real on-disk scan DB).
    /// </summary>
    public class StoragePageWebPartTests : IClassFixture<ScanContextFixture>
    {
        private readonly ScanContextFixture fixture;

        public StoragePageWebPartTests(ScanContextFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task StorePageWebParts_BulkInsert_RowsPersisted()
        {
            var scanId = Guid.NewGuid();
            var siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";
            var pageUrl = "/SitePages/Home.aspx";

            var webParts = new List<ClassicPageWebPart>
            {
                NewWebPart(scanId, siteUrl, pageUrl, index: 0, type: "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart", isMappable: true),
                NewWebPart(scanId, siteUrl, pageUrl, index: 1, type: "Microsoft.SharePoint.WebPartPages.XsltListViewWebPart", isMappable: true),
                NewWebPart(scanId, siteUrl, pageUrl, index: 2, type: "Contoso.Custom.WebPart", isMappable: false),
            };

            using (var context = fixture.CreateContext())
            {
                await StorageManager.StorePageWebPartsAsync(context, webParts);
            }

            using (var context = fixture.CreateContext())
            {
                var stored = context.ClassicPageWebParts
                    .Where(wp => wp.ScanId == scanId && wp.PageUrl == pageUrl)
                    .OrderBy(wp => wp.WebPartIndex)
                    .ToList();

                stored.Should().HaveCount(3);

                stored[0].WebPartType.Should().Be("Microsoft.SharePoint.WebPartPages.ContentEditorWebPart");
                stored[0].WebPartTypeShort.Should().Be("ContentEditorWebPart");
                stored[0].ZoneId.Should().Be("Header");
                stored[0].Row.Should().Be(1);
                stored[0].Column.Should().Be(2);
                stored[0].Order.Should().Be(0);
                stored[0].IsMappable.Should().BeTrue();

                stored[2].WebPartType.Should().Be("Contoso.Custom.WebPart");
                stored[2].IsMappable.Should().BeFalse();
            }
        }

        [Fact]
        public async Task StorePageWebParts_EmptyList_NoOp()
        {
            var scanId = Guid.NewGuid();
            var siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";

            // Neither an empty list nor a null list should throw or write any rows.
            using (var context = fixture.CreateContext())
            {
                await StorageManager.StorePageWebPartsAsync(context, new List<ClassicPageWebPart>());
                await StorageManager.StorePageWebPartsAsync(context, null);
            }

            using (var context = fixture.CreateContext())
            {
                context.ClassicPageWebParts.Count(wp => wp.ScanId == scanId).Should().Be(0);
            }
        }

        [Fact]
        public async Task StorePageInformation_EnrichedColumns_Persisted()
        {
            var scanId = Guid.NewGuid();
            var siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";
            var pageUrl = "/SitePages/Home.aspx";

            var pages = new List<ClassicPage>
            {
                new()
                {
                    ScanId = scanId,
                    SiteUrl = siteUrl,
                    WebUrl = siteUrl,
                    PageUrl = pageUrl,
                    PageName = "Home.aspx",
                    PageType = "WikiPage",
                    RemediationCode = "CP2",
                    // Page-scan enrichment columns (the fields T5-T7/T10 stamp before persistence).
                    Layout = "TwoColumns",
                    HomePage = true,
                    UncustomizedHomePage = true,
                    ModifiedBy = "jane@contoso.com",
                    WebPartCount = 4,
                    MappingPercentage = 75,
                    UnmappedWebParts = "ContosoCustomWebPart",
                },
            };

            using (var context = fixture.CreateContext())
            {
                await StorageManager.StorePageInformationAsync(context, pages);
            }

            using (var context = fixture.CreateContext())
            {
                var page = context.ClassicPages.Single(p => p.ScanId == scanId && p.PageUrl == pageUrl);

                page.Layout.Should().Be("TwoColumns");
                page.HomePage.Should().BeTrue();
                page.UncustomizedHomePage.Should().BeTrue();
                page.ModifiedBy.Should().Be("jane@contoso.com");
                page.WebPartCount.Should().Be(4);
                page.MappingPercentage.Should().Be(75);
                page.UnmappedWebParts.Should().Be("ContosoCustomWebPart");
                page.RemediationCode.Should().Be("CP2");
            }
        }

        private static ClassicPageWebPart NewWebPart(Guid scanId, string siteUrl, string pageUrl, int index, string type, bool isMappable)
        {
            return new ClassicPageWebPart
            {
                ScanId = scanId,
                SiteUrl = siteUrl,
                WebUrl = siteUrl,
                PageUrl = pageUrl,
                WebPartIndex = index,
                WebPartType = type,
                WebPartTypeShort = type.Split(',')[0].Split('.').Last(),
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
