using System.Collections.Generic;
using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages
{
    /// <summary>
    /// T13 — the pure page-usage parsing/mapping (recent + lifetime views and their unique-user counts).
    /// The live CSOM search query (<c>PageUsageAnalyzer.QueryPageUsageAsync</c>) is integration-only
    /// (→ T15); here only the row parsing, the skip-usage gate and the search-path builder are covered.
    /// </summary>
    public class PageUsageAnalyzerTests
    {
        private static Dictionary<string, string> SearchRow(
            string recent = "10", string recentUsers = "4", string lifeTime = "123", string lifeTimeUsers = "45")
        {
            return new Dictionary<string, string>
            {
                ["OriginalPath"] = "https://contoso.sharepoint.com/sites/foo/SitePages/Page.aspx",
                ["ViewsRecent"] = recent,
                ["ViewsRecentUniqueUsers"] = recentUsers,
                ["ViewsLifeTime"] = lifeTime,
                ["ViewsLifeTimeUniqueUsers"] = lifeTimeUsers,
            };
        }

        [Fact]
        public void Usage_ParseSearchRow_MapsViewFields()
        {
            var usage = PageUsageAnalyzer.ParseSearchRow(SearchRow());

            usage.ViewsRecent.Should().Be(10);
            usage.ViewsRecentUniqueUsers.Should().Be(4);
            usage.ViewsLifeTime.Should().Be(123);
            usage.ViewsLifeTimeUniqueUsers.Should().Be(45);
        }

        [Fact]
        public void Usage_ApplyUsage_MapsViewFieldsOntoPage()
        {
            var page = new ClassicPage();

            PageUsageAnalyzer.ApplyUsage(page, SearchRow(), skipUsageInformation: false);

            page.ViewsRecent.Should().Be(10);
            page.ViewsRecentUniqueUsers.Should().Be(4);
            page.ViewsLifeTime.Should().Be(123);
            page.ViewsLifeTimeUniqueUsers.Should().Be(45);
        }

        [Fact]
        public void Usage_SkipUsageInformation_LeavesZero()
        {
            // With the skip flag set the view columns stay at their default 0 even when a row is supplied.
            var page = new ClassicPage();

            PageUsageAnalyzer.ApplyUsage(page, SearchRow(), skipUsageInformation: true);

            page.ViewsRecent.Should().Be(0);
            page.ViewsRecentUniqueUsers.Should().Be(0);
            page.ViewsLifeTime.Should().Be(0);
            page.ViewsLifeTimeUniqueUsers.Should().Be(0);
        }

        [Fact]
        public void Usage_ApplyUsage_NullRow_LeavesZero()
        {
            // A page that is not in the search index yields no row → counts stay 0.
            var page = new ClassicPage();

            PageUsageAnalyzer.ApplyUsage(page, null, skipUsageInformation: false);

            page.ViewsRecent.Should().Be(0);
            page.ViewsRecentUniqueUsers.Should().Be(0);
            page.ViewsLifeTime.Should().Be(0);
            page.ViewsLifeTimeUniqueUsers.Should().Be(0);
        }

        [Fact]
        public void Usage_ParseSearchRow_MissingOrEmptyValues_DefaultToZero()
        {
            var row = new Dictionary<string, string>
            {
                ["ViewsRecent"] = "",               // empty
                ["ViewsLifeTime"] = "not-a-number", // unparseable
                // ViewsRecentUniqueUsers / ViewsLifeTimeUniqueUsers absent entirely
            };

            var usage = PageUsageAnalyzer.ParseSearchRow(row);

            usage.ViewsRecent.Should().Be(0);
            usage.ViewsRecentUniqueUsers.Should().Be(0);
            usage.ViewsLifeTime.Should().Be(0);
            usage.ViewsLifeTimeUniqueUsers.Should().Be(0);
        }

        [Fact]
        public void BuildPageSearchPath_RegularPage_PrefixesSiteAuthority()
        {
            // A regular page is looked up by its absolute URL (site authority + server-relative page path).
            string path = PageUsageAnalyzer.BuildPageSearchPath(
                "https://contoso.sharepoint.com/sites/foo", "/subweb", "/sites/foo/subweb/SitePages/Page.aspx", isHomePage: false);

            path.Should().Be("https://contoso.sharepoint.com/sites/foo/subweb/SitePages/Page.aspx");
        }

        [Fact]
        public void BuildPageSearchPath_HomePage_UsesWebUrl()
        {
            // The home page is indexed under the web URL itself, not its SitePages/...aspx path.
            string path = PageUsageAnalyzer.BuildPageSearchPath(
                "https://contoso.sharepoint.com/sites/foo", "/subweb", "/sites/foo/subweb/SitePages/Home.aspx", isHomePage: true);

            path.Should().Be("https://contoso.sharepoint.com/sites/foo/subweb");
        }
    }
}
