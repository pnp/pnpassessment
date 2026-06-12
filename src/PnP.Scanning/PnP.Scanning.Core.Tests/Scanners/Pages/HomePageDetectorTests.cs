using System.Collections.Generic;
using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Scanners.WebPartMapping;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages
{
    /// <summary>
    /// T10 — home-page semantics. The pure pieces are exercised here directly (no CSOM, no live
    /// SharePoint):
    /// <list type="bullet">
    /// <item><description><see cref="HomePageDetector.IsHomePage"/> — the welcome-page url compare.</description></item>
    /// <item><description><see cref="HomePageDetector.IsHtmlUncustomized"/> — the default-home-page HTML
    /// compare (the fallback used when the CSOM <c>CanModernizeHomepage</c> API is unavailable).</description></item>
    /// <item><description><see cref="HomePageDetector.IsDefaultTeamSiteWebPartSet"/> and
    /// <see cref="HomePageDetector.IsUncustomizedHomePageFallback"/> — the rest of the fallback decision.</description></item>
    /// </list>
    /// The reliable CSOM <c>web.CanModernizeHomepage</c> path (a bare property read) is wired per-web with
    /// the rest of the page scan and is covered by the integration test (T15).
    /// </summary>
    public class HomePageDetectorTests
    {
        // Short type names the legacy categorization keys on, expanded to fully assembly-qualified types
        // (TypeShort() drops everything after the first comma, recovering the short name).
        private const string XsltListViewType = "Microsoft.SharePoint.WebPartPages.XsltListViewWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string ListViewType = "Microsoft.SharePoint.WebPartPages.ListViewWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string GettingStartedType = "Microsoft.SharePoint.WebPartPages.GettingStartedWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string SiteFeedType = "Microsoft.SharePoint.Portal.WebControls.SiteFeedWebPart, Microsoft.SharePoint.Portal, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string ContentEditorType = "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

        // Captured default team-site home page WikiField HTML: the normalized out-of-the-box layout with
        // realistic whitespace and per-web-part GUIDs (both stripped during the uncustomized compare).
        // "DefaultHtml" = SiteFeed + XsltListView + GettingStarted; "DefaultRootHtml" = the root site variant.
        private const string DefaultHomeHtml = "<divclass=\"<tableid=\"layoutsTable\"style=\"width&#58;100%;\">\n            <tbody>\n            <trstyle=\"vertical-align&#58;top;\">\n            <tdcolspan=\"2\">\n            <divclass=\"ms-rte-layoutszone-outer\"style=\"width&#58;100%;\">\n            <divclass=\"ms-rte-layoutszone-inner\"style=\"word-wrap&#58;break-word;margin&#58;0px;border&#58;0px;\">\n            <divclass=\"ms-rtestate-readms-rte-wpbox\">\n            <divclass=\"ms-rtestate-read\"id=\"div_CCCCCCCC-1111-2222-3333-444444444444\">\n            </div>\n            <divclass=\"ms-rtestate-read\"id=\"vid_DDDDDDDD-1111-2222-3333-444444444444\"style=\"display&#58;none;\">\n            </div>\n            </div>\n            </div>\n            </div>\n            </td>\n            </tr>\n            <trstyle=\"vertical-align&#58;top;\">\n            <tdstyle=\"width&#58;49.95%;\">\n            <divclass=\"ms-rte-layoutszone-outer\"style=\"width&#58;100%;\">\n            <divclass=\"ms-rte-layoutszone-inner\"style=\"word-wrap&#58;break-word;margin&#58;0px;border&#58;0px;\">\n            <divclass=\"ms-rtestate-readms-rte-wpbox\">\n            <divclass=\"ms-rtestate-read\"id=\"div_CCCCCCCC-1111-2222-3333-444444444444\">\n            </div>\n            <divclass=\"ms-rtestate-read\"id=\"vid_DDDDDDDD-1111-2222-3333-444444444444\"style=\"display&#58;none;\">\n            </div>\n            </div>\n            </div>\n            </div>\n            </td>\n            <tdclass=\"ms-wiki-columnSpacing\"style=\"width&#58;49.95%;\">\n            <divclass=\"ms-rte-layoutszone-outer\"style=\"width&#58;100%;\">\n            <divclass=\"ms-rte-layoutszone-inner\"style=\"word-wrap&#58;break-word;margin&#58;0px;border&#58;0px;\">\n            <divclass=\"ms-rtestate-readms-rte-wpbox\">\n            <divclass=\"ms-rtestate-read\"id=\"div_CCCCCCCC-1111-2222-3333-444444444444\">\n            </div>\n            <divclass=\"ms-rtestate-read\"id=\"vid_DDDDDDDD-1111-2222-3333-444444444444\"style=\"display&#58;none;\">\n            </div>\n            </div>\n            </div>\n            </div>\n            </td>\n            </tr>\n            </tbody>\n            </table>\n            <spanid=\"layoutsData\"style=\"display&#58;none;\">true,false,2</span>\n            </div>";

        private const string DefaultRootHomeHtml = "<divclass=\"<tableid=\"layoutsTable\"style=\"width&#58;100%;\">\n            <tbody>\n            <trstyle=\"vertical-align&#58;top;\">\n            <tdstyle=\"width&#58;100%;padding&#58;0px;\">\n            <divclass=\"ms-rte-layoutszone-outer\"style=\"width&#58;100%;\">\n            <divclass=\"ms-rte-layoutszone-inner\"style=\"min-height&#58;60px;word-wrap&#58;break-word;margin&#58;0px;border&#58;0px;\">\n            <divclass=\"ms-rtestate-readms-rte-wpbox\">\n            <divclass=\"ms-rtestate-read\"id=\"div_AAAAAAAA-1111-2222-3333-444444444444\">\n            </div>\n            <divclass=\"ms-rtestate-read\"id=\"vid_BBBBBBBB-1111-2222-3333-444444444444\"style=\"display&#58;none;\">\n            </div>\n            </div>\n            <divclass=\"ms-rtestate-readms-rte-wpbox\">\n            <divclass=\"ms-rtestate-read\"id=\"div_AAAAAAAA-1111-2222-3333-444444444444\">\n            </div>\n            <divclass=\"ms-rtestate-read\"id=\"vid_BBBBBBBB-1111-2222-3333-444444444444\"style=\"display&#58;none;\">\n            </div>\n            </div>\n            </div>\n            </div>\n            </td>\n            </tr>\n            </tbody>\n            </table>\n            <spanid=\"layoutsData\"style=\"display&#58;none;\">false,false,1</span>\n            </div>";

        private static WebPartEntity WebPart(string fullType)
        {
            return new WebPartEntity { Type = fullType };
        }

        #region IsHomePage

        [Fact]
        public void HomePage_UrlEqualsWelcomePage_True()
        {
            HomePageDetector.IsHomePage("/sites/team/SitePages/Home.aspx", "SitePages/Home.aspx").Should().BeTrue();
        }

        [Fact]
        public void HomePage_DifferentUrl_False()
        {
            HomePageDetector.IsHomePage("/sites/team/SitePages/About.aspx", "SitePages/Home.aspx").Should().BeFalse();
        }

        [Fact]
        public void HomePage_IsCaseInsensitive()
        {
            HomePageDetector.IsHomePage("/sites/team/SitePages/HOME.ASPX", "SitePages/Home.aspx").Should().BeTrue();
        }

        [Fact]
        public void HomePage_EmptyWelcomePage_DefaultsToDefaultAspx()
        {
            // A web with no welcome page (e.g. a web part page home page) defaults to default.aspx.
            HomePageDetector.IsHomePage("/sites/team/default.aspx", "").Should().BeTrue();
            HomePageDetector.IsHomePage("/sites/team/default.aspx", null).Should().BeTrue();
            HomePageDetector.IsHomePage("/sites/team/SitePages/Home.aspx", "").Should().BeFalse();
        }

        [Fact]
        public void HomePage_NullPageUrl_False()
        {
            HomePageDetector.IsHomePage(null, "SitePages/Home.aspx").Should().BeFalse();
        }

        [Fact]
        public void NormalizeWelcomePage_EmptyOrNull_ReturnsDefaultAspx()
        {
            HomePageDetector.NormalizeWelcomePage("").Should().Be("default.aspx");
            HomePageDetector.NormalizeWelcomePage(null).Should().Be("default.aspx");
            HomePageDetector.NormalizeWelcomePage("SitePages/Home.aspx").Should().Be("SitePages/Home.aspx");
        }

        #endregion

        #region IsHtmlUncustomized

        [Fact]
        public void UncustomizedHome_DefaultStsHtml_True()
        {
            HomePageDetector.IsHtmlUncustomized(DefaultHomeHtml).Should().BeTrue();
        }

        [Fact]
        public void UncustomizedHome_DefaultRootStsHtml_True()
        {
            HomePageDetector.IsHtmlUncustomized(DefaultRootHomeHtml).Should().BeTrue();
        }

        [Fact]
        public void UncustomizedHome_CustomizedHtml_False()
        {
            // The same default layout with an added paragraph of real content no longer matches.
            var customized = DefaultHomeHtml.Replace("<tbody>", "<tbody><p>Welcome to our customized team site!</p>");
            HomePageDetector.IsHtmlUncustomized(customized).Should().BeFalse();
        }

        [Fact]
        public void UncustomizedHome_NullOrEmptyHtml_False()
        {
            HomePageDetector.IsHtmlUncustomized(null).Should().BeFalse();
            HomePageDetector.IsHtmlUncustomized("").Should().BeFalse();
        }

        #endregion

        #region IsDefaultTeamSiteWebPartSet

        [Fact]
        public void DefaultWebPartSet_SingleXsltListViewPlusDefaults_True()
        {
            // The out-of-the-box team site home page: a single XsltListView plus the getting-started/feed parts.
            var webParts = new List<WebPartEntity>
            {
                WebPart(GettingStartedType),
                WebPart(SiteFeedType),
                WebPart(XsltListViewType),
            };

            HomePageDetector.IsDefaultTeamSiteWebPartSet(webParts).Should().BeTrue();
        }

        [Fact]
        public void DefaultWebPartSet_NoListViews_False()
        {
            // The list view web part was removed → no longer the default set.
            var webParts = new List<WebPartEntity>
            {
                WebPart(GettingStartedType),
            };

            HomePageDetector.IsDefaultTeamSiteWebPartSet(webParts).Should().BeFalse();
        }

        [Fact]
        public void DefaultWebPartSet_MultipleListViews_False()
        {
            var webParts = new List<WebPartEntity>
            {
                WebPart(XsltListViewType),
                WebPart(XsltListViewType),
                WebPart(ListViewType),
            };

            HomePageDetector.IsDefaultTeamSiteWebPartSet(webParts).Should().BeFalse();
        }

        [Fact]
        public void DefaultWebPartSet_ContainsOtherWebPart_False()
        {
            var webParts = new List<WebPartEntity>
            {
                WebPart(XsltListViewType),
                WebPart(ContentEditorType),
            };

            HomePageDetector.IsDefaultTeamSiteWebPartSet(webParts).Should().BeFalse();
        }

        [Fact]
        public void DefaultWebPartSet_NullOrEmpty_False()
        {
            // No web parts at all is not the default set (it requires exactly one XsltListView).
            HomePageDetector.IsDefaultTeamSiteWebPartSet(null).Should().BeFalse();
            HomePageDetector.IsDefaultTeamSiteWebPartSet(new List<WebPartEntity>()).Should().BeFalse();
        }

        #endregion

        #region IsUncustomizedHomePageFallback

        private static List<WebPartEntity> DefaultHomeWebParts() => new()
        {
            WebPart(GettingStartedType),
            WebPart(XsltListViewType),
        };

        // Builds the fallback call with all conditions satisfied; each test overrides one input to flip it.
        private static bool Evaluate(
            bool isHomePage = true,
            string webTemplate = "STS",
            int webConfiguration = 0,
            bool publishingSiteFeatureEnabled = false,
            bool publishingWebFeatureEnabled = false,
            bool homePageModernizationOptedOut = false,
            bool siteWasGroupified = false,
            string masterUrl = "https://contoso.sharepoint.com/sites/team/_catalogs/masterpage/seattle.master",
            string pageName = "Home.aspx",
            string localizedHomePageName = "Home.aspx",
            string wikiHtml = DefaultHomeHtml,
            List<WebPartEntity> webParts = null,
            string contentTypeDisplayFormTemplateName = "WikiEditForm")
        {
            return HomePageDetector.IsUncustomizedHomePageFallback(
                isHomePage, webTemplate, webConfiguration, publishingSiteFeatureEnabled, publishingWebFeatureEnabled,
                homePageModernizationOptedOut, siteWasGroupified, masterUrl, pageName, localizedHomePageName,
                wikiHtml, webParts ?? DefaultHomeWebParts(), contentTypeDisplayFormTemplateName);
        }

        [Fact]
        public void Fallback_DefaultStsHomePage_True()
        {
            Evaluate().Should().BeTrue();
        }

        [Fact]
        public void Fallback_NotHomePage_False()
        {
            Evaluate(isHomePage: false).Should().BeFalse();
        }

        [Fact]
        public void Fallback_NotStsTeamSite_False()
        {
            Evaluate(webTemplate: "SITEPAGEPUBLISHING").Should().BeFalse();
            Evaluate(webConfiguration: 1).Should().BeFalse();
        }

        [Fact]
        public void Fallback_PublishingOrOptedOut_False()
        {
            Evaluate(publishingSiteFeatureEnabled: true).Should().BeFalse();
            Evaluate(publishingWebFeatureEnabled: true).Should().BeFalse();
            Evaluate(homePageModernizationOptedOut: true).Should().BeFalse();
        }

        [Fact]
        public void Fallback_Groupified_False()
        {
            Evaluate(siteWasGroupified: true).Should().BeFalse();
        }

        [Fact]
        public void Fallback_NonDefaultMasterPage_False()
        {
            Evaluate(masterUrl: "https://contoso.sharepoint.com/sites/team/_catalogs/masterpage/custom.master").Should().BeFalse();
            Evaluate(masterUrl: null).Should().BeFalse();
        }

        [Fact]
        public void Fallback_NotTheDefaultHomePageName_False()
        {
            Evaluate(pageName: "Welcome.aspx").Should().BeFalse();
        }

        [Fact]
        public void Fallback_CustomizedHtml_False()
        {
            Evaluate(wikiHtml: "<div>totally custom content</div>").Should().BeFalse();
        }

        [Fact]
        public void Fallback_NonDefaultWebPartSet_False()
        {
            Evaluate(webParts: new List<WebPartEntity> { WebPart(ContentEditorType) }).Should().BeFalse();
        }

        [Fact]
        public void Fallback_NotWikiEditFormContentType_False()
        {
            Evaluate(contentTypeDisplayFormTemplateName: "CustomEditForm").Should().BeFalse();
        }

        #endregion
    }
}
