using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using FluentAssertions;
using PnP.Scanning.Core.ClassicPages.Compatibility;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Text;
using System.Text.Json;
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
        /// <summary>Verifies telemetry log page scan builds expected payload.</summary>
        [Fact]
        public void Telemetry_LogPageScan_BuildsExpectedPayload()
        {
            using ScanContextFixture fixture = new ScanContextFixture();
            Guid scanId = Guid.NewGuid();
            string siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";

            // One page per classic type; three carry web parts (75 / 100 / 50), three carry none.
            // The ASPX page is the (uncustomized) home page.
            List<ClassicPage> pages = new List<ClassicPage>
            {
                NewPage(
                    scanId,
                    siteUrl,
                    "/Pages/a.aspx",
                    ClassicPageRules.WikiPage,
                    webPartCount: 4,
                    mappingPercentage: 75),
                NewPage(
                    scanId,
                    siteUrl,
                    "/Pages/b.aspx",
                    ClassicPageRules.WebPartPage,
                    webPartCount: 2,
                    mappingPercentage: 100),
                NewPage(
                    scanId,
                    siteUrl,
                    "/Pages/c.aspx",
                    ClassicPageRules.PublishingPage,
                    webPartCount: 3,
                    mappingPercentage: 50),
                NewPage(
                    scanId,
                    siteUrl,
                    "/Lists/Posts/d.aspx",
                    ClassicPageRules.BlogPage,
                    webPartCount: 0,
                    mappingPercentage: 100),
                NewPage(
                    scanId,
                    siteUrl,
                    "/default.aspx",
                    ClassicPageRules.ASPXPage,
                    webPartCount: 0,
                    mappingPercentage: 100,
                    homePage: true,
                    uncustomizedHomePage: true),
                NewPage(
                    scanId,
                    siteUrl,
                    "/Lists/Posts/f.aspx",
                    ClassicPageRules.DelveBlogPage,
                    webPartCount: 0,
                    mappingPercentage: 100),
            };

            // Five web part rows total across the three pages that carry parts.
            List<ClassicPageWebPart> webParts = new List<ClassicPageWebPart>
            {
                NewWebPart(scanId, siteUrl, "/Pages/a.aspx", 0, "ContentEditorWebPart"),
                NewWebPart(scanId, siteUrl, "/Pages/a.aspx", 1, "ContentEditorWebPart"),
                NewWebPart(scanId, siteUrl, "/Pages/b.aspx", 0, "XsltListViewWebPart"),
                NewWebPart(scanId, siteUrl, "/Pages/c.aspx", 0, "Contoso.Custom.WebPart"),
                NewWebPart(scanId, siteUrl, "/Pages/c.aspx", 1, "Contoso.Custom.WebPart"),
            };

            // Scan-wide unique inventory: 2 mapped types, 1 unmapped type.
            List<ClassicWebPartUnique> uniques = new List<ClassicWebPartUnique>
            {
                new()
                {
                    ScanId = scanId, WebPartType = "ContentEditorWebPart", InMappingFile = true, PageCount = 1
                },
                new() { ScanId = scanId, WebPartType = "XsltListViewWebPart", InMappingFile = true, PageCount = 1 },
                new()
                {
                    ScanId = scanId,
                    WebPartType = "Contoso.Custom.WebPart",
                    InMappingFile = false,
                    PageCount = 1
                },
            };

            using (ScanContext context = fixture.CreateContext())
            {
                context.ClassicPages.AddRange(pages);
                context.ClassicPageWebParts.AddRange(webParts);
                context.ClassicWebPartUniques.AddRange(uniques);
                context.SaveChanges();
            }

            Dictionary<string, double> metric = new Dictionary<string, double>();
            using (ScanContext context = fixture.CreateContext())
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
            metric["ClassicPageMappableWebPartPagesCount"].Should().Be(1); // b (100%)
            metric["ClassicPageUnmappedWebPartPagesCount"].Should().Be(2); // a (75), c (50)
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

        /// <summary>
        /// Verifies that an assessment with a native analysis counts the pages the report selects, under the page
        /// type the report gives them, with only the web parts on those pages, and that an assessment without a
        /// native analysis keeps counting every stored page row.
        /// </summary>
        [Fact]
        public void Telemetry_LogPageScan_NativeAnalysis_CountsTheReportSelection()
        {
            const string wikiHandler = "Microsoft.SharePoint.WebPartPages.WikiEditPage";
            const string webPartHandler = "Microsoft.SharePoint.WebPartPages.WebPartPage";
            using ScanContextFixture fixture = new ScanContextFixture();
            Guid scanId = Guid.NewGuid();
            Guid site = Guid.NewGuid();
            string siteUrl = $"https://contoso.sharepoint.com/sites/{scanId:N}";

            SPFile wiki = NewFile(scanId, site, siteUrl, "/SitePages/a.aspx", wikiHandler, false, false);
            SPFile webPart = NewFile(scanId, site, siteUrl, "/Pages/b.aspx", webPartHandler, null, null);
            SPFile download = NewFile(scanId, site, siteUrl, "/Pages/c.aspx", webPartHandler, true, false);
            SPFile listView = NewFile(scanId, site, siteUrl, "/Lists/x/AllItems.aspx", webPartHandler, null, true);
            SPFile layout = NewFile(
                scanId,
                site,
                siteUrl,
                "/_catalogs/masterpage/l.aspx",
                "Microsoft.SharePoint.Publishing.PublishingLayoutPage",
                false,
                false);
            SPFile document = NewFile(scanId, site, siteUrl, "/Pages/d.docx", wikiHandler, false, false);

            var reported = ClassicReportCsvProjection.SelectedFilePageTypes(
                new[] { wiki, webPart, download, listView, layout, document });

            reported.Should().HaveCount(2);
            reported[(site, wiki.FileUniqueId)].Should().Be(ClassicPageRules.WikiPage);
            reported[(site, webPart.FileUniqueId)].Should().Be(ClassicPageRules.WebPartPage);

            // The stored page type differs from the report's on purpose: the report's type is the one counted.
            ClassicPage post = NewPage(scanId, siteUrl, "/Lists/Posts/Post.aspx?ID=7", "ASPXPage", 0, 100);
            post.SiteCollectionId = site;
            post.ListItemId = 7;
            List<ClassicPage> pages = new List<ClassicPage>
            {
                NewStoredPage(wiki, ClassicPageRules.WebPartPage, 4, 75),
                NewStoredPage(webPart, ClassicPageRules.ASPXPage, 2, 100, homePage: true),
                NewStoredPage(download, ClassicPageRules.WebPartPage, 1, 0),
                NewStoredPage(listView, ClassicPageRules.WebPartPage, 1, 100),
                NewStoredPage(layout, ClassicPageRules.ASPXPage, 3, 100),
                post,
            };
            List<ClassicPageWebPart> webParts = new List<ClassicPageWebPart>
            {
                NewWebPart(scanId, siteUrl, wiki.ServerRelativeUrl, 0, "A"),
                NewWebPart(scanId, siteUrl, wiki.ServerRelativeUrl, 1, "A"),
                NewWebPart(scanId, siteUrl, webPart.ServerRelativeUrl, 0, "B"),
                NewWebPart(scanId, siteUrl, download.ServerRelativeUrl, 0, "C"),
                NewWebPart(scanId, siteUrl, layout.ServerRelativeUrl, 0, "A"),
            };
            List<ClassicWebPartUnique> uniques = new List<ClassicWebPartUnique>
            {
                new() { ScanId = scanId, WebPartType = "A", InMappingFile = true, PageCount = 2 },
                new() { ScanId = scanId, WebPartType = "B", InMappingFile = true, PageCount = 1 },
                new() { ScanId = scanId, WebPartType = "C", InMappingFile = false, PageCount = 1 },
            };
            using (ScanContext context = fixture.CreateContext())
            {
                context.ClassicPages.AddRange(pages);
                context.ClassicPageWebParts.AddRange(webParts);
                context.ClassicWebPartUniques.AddRange(uniques);
                context.SaveChanges();
            }

            Dictionary<string, double> metric = new Dictionary<string, double>();
            Dictionary<string, double> retained = new Dictionary<string, double>();
            using (ScanContext context = fixture.CreateContext())
            {
                TelemetryManager.BuildClassicMetrics(context, metric, reported);
                ClassicReportCsvProjection.SelectedFilePageTypes(context).Should().BeNull();
                TelemetryManager.BuildClassicMetrics(context, retained);
            }

            // The wiki page, the web part page and the post are reported; the download, the list view and the
            // page layout are left out, together with their web parts.
            metric["ClassicPageCount"].Should().Be(3);
            metric["ClassicPageWikiCount"].Should().Be(1);
            metric["ClassicPageWebPartPageCount"].Should().Be(1);
            metric["ClassicPageASPXCount"].Should().Be(0);
            metric["ClassicPagePublishingCount"].Should().Be(0);
            metric["ClassicPageBlogCount"].Should().Be(1);
            metric["ClassicPageDelveBlogCount"].Should().Be(0);
            metric["ClassicPageHomePageCount"].Should().Be(1);
            metric["ClassicPageUncustomizedHomePageCount"].Should().Be(0);
            metric["ClassicPagePagesWithWebPartsCount"].Should().Be(2);
            metric["ClassicPageMappableWebPartPagesCount"].Should().Be(1);
            metric["ClassicPageUnmappedWebPartPagesCount"].Should().Be(1);
            metric["ClassicPageAvgMappingPercentage"].Should().BeApproximately(87.5, 0.0001);
            metric["ClassicPageWebPartCount"].Should().Be(3);
            metric["ClassicPageUniqueWebPartTypeCount"].Should().Be(2);
            metric["ClassicPageMappableWebPartTypeCount"].Should().Be(2);
            metric["ClassicPageUnmappedWebPartTypeCount"].Should().Be(0);

            // Same metric names either way; an assessment without a native analysis counts every stored row.
            retained.Keys.Should().BeEquivalentTo(metric.Keys);
            retained["ClassicPageCount"].Should().Be(6);
            retained["ClassicPageWebPartCount"].Should().Be(5);
            retained["ClassicPageUniqueWebPartTypeCount"].Should().Be(3);
        }

        /// <summary>
        /// Verifies that an assessment event carries exactly the existing property names, and that the tenant name,
        /// the site list, the site file, the application id and the certificate location never leave as values.
        /// </summary>
        [Fact]
        public void Telemetry_ScanEvent_CarriesTheExistingPropertiesAndNoAssessmentInput()
        {
            Guid scanId = Guid.NewGuid();
            Guid tenantId = Guid.NewGuid();
            PnP.Scanning.Core.Storage.Scan scan = new PnP.Scanning.Core.Storage.Scan
            {
                ScanId = scanId,
                CLIMode = "Classic",
                CLITenant = "contoso.sharepoint.com",
                CLITenantId = tenantId.ToString().ToUpperInvariant(),
                CLIEnvironment = "Production",
                CLISiteList = "https://contoso.sharepoint.com/sites/hr",
                CLISiteFile = @"C:\Users\megan\sites.txt",
                CLIAuthMode = "Application",
                CLIApplicationId = "application-id-value",
                CLICertPath = "My|CurrentUser|certificate-thumbprint-value",
                CLICertFile = @"C:\Users\megan\certificate.pfx",
                CLICertFilePassword = "certificate-password-value",
                CLIThreads = 10,
            };
            Dictionary<string, string> properties = new Dictionary<string, string>();

            TelemetryManager.PopulateDefaultProperties(properties, scan, "1.17.0.0");
            TelemetryManager.PopulateScanProperties(properties, scanId, scan);

            properties.Should()
                .BeEquivalentTo(
                    new Dictionary<string, string>
                    {
                        ["Version"] = "1.17.0.0",
                        ["AADTenantId"] = tenantId.ToString(),
                        ["Mode"] = "Classic",
                        ["ScanId"] = scanId.ToString(),
                        ["UsesTenant"] = "True",
                        ["Environment"] = "Production",
                        ["AuthMode"] = "Application",
                        ["UsesSitesList"] = "True",
                        ["UsesSitesFile"] = "True",
                        ["Threads"] = "10",
                        ["UsesCertPath"] = "True",
                        ["UsesCertFile"] = "True",
                    });
            properties.Values.Should()
                .NotContain(value =>
                    value.Contains("contoso") || value.Contains("megan") || value.Contains("certificate") ||
                    value.Contains("application-id"));
        }

        /// <summary>Builds a stored physical file with its handler, served-as flag and list view evidence.</summary>
        private static SPFile NewFile(
            Guid scanId,
            Guid site,
            string siteUrl,
            string url,
            string handler,
            bool? blocked,
            bool? listView)
        {
            return new SPFile
            {
                ScanId = scanId,
                SiteCollectionId = site,
                FileUniqueId = Guid.NewGuid(),
                SiteUrl = siteUrl,
                WebUrl = "/",
                ServerRelativeUrl = url,
                FileName = Path.GetFileName(url),
                NoExecuteRaw = blocked == null ? null : blocked.Value ? "1" : "0",
                NoExecuteReadState = blocked.HasValue ? "Available" : "NotRequested",
                NoExecuteSource = blocked.HasValue ? "SPO.ListItem.NoExecute" : null,
                AspxEvidenceJson = JsonSerializer.Serialize(
                    new AspxPageEvidence(
                        "Available",
                        null,
                        handler,
                        "Declared",
                        null,
                        null,
                        119,
                        false,
                        listView,
                        false,
                        listView.HasValue ? "Complete" : "Partial",
                        DateTime.UnixEpoch,
                        Encoding.UTF8.GetBytes("<%@ Page Inherits=\"" + handler + "\" %>"))),
            };
        }

        /// <summary>Builds the stored page row of a physical file.</summary>
        private static ClassicPage NewStoredPage(
            SPFile file,
            string pageType,
            int webPartCount,
            double mappingPercentage,
            bool homePage = false)
        {
            ClassicPage page = NewPage(
                file.ScanId,
                file.SiteUrl,
                file.ServerRelativeUrl,
                pageType,
                webPartCount,
                mappingPercentage,
                homePage);
            page.SiteCollectionId = file.SiteCollectionId;
            page.FileUniqueId = file.FileUniqueId;
            return page;
        }

        /// <summary>Verifies new page.</summary>
        private static ClassicPage NewPage(
            Guid scanId,
            string siteUrl,
            string pageUrl,
            string pageType,
            int webPartCount,
            double mappingPercentage,
            bool homePage = false,
            bool uncustomizedHomePage = false)
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

        /// <summary>Verifies new web part.</summary>
        private static ClassicPageWebPart NewWebPart(
            Guid scanId,
            string siteUrl,
            string pageUrl,
            int index,
            string type)
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