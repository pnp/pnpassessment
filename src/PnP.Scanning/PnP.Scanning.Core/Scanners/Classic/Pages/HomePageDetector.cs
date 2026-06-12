using System.Text.RegularExpressions;
using PnP.Scanning.Core.Scanners.WebPartMapping;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Home-page semantics for a discovered classic page, ported from the Modernization Scanner's
    /// <c>PageAnalyzer</c>:
    /// <list type="bullet">
    /// <item><description><b>Home page</b> — whether the page is the web's welcome page
    /// (<see cref="IsHomePage"/>, a pure compare against <c>web.WelcomePage</c>).</description></item>
    /// <item><description><b>Uncustomized home page</b> — whether the page is a still-default
    /// "out of the box" team-site home page. The reliable answer comes from the CSOM
    /// <c>web.CanModernizeHomepage</c> API (a bare property read, so it has no logic to port — it is
    /// part of the per-web CSOM wiring assembled in the persistence task and exercised by the
    /// integration test). When that API is unavailable the legacy scanner falls back to an
    /// HTML/web-part heuristic, which <i>does</i> carry logic; that whole fallback decision is ported
    /// here as the pure <see cref="IsUncustomizedHomePageFallback"/> so it is unit-testable.</description></item>
    /// </list>
    /// Everything in this module is pure (no CSOM): the caller resolves the CSOM inputs (welcome page,
    /// feature flags, master page url, content type, localized home-page name) and passes them in.
    /// </summary>
    internal static class HomePageDetector
    {
        // Default welcome page name used when web.WelcomePage is empty (e.g. a web part page home page),
        // matching the legacy PageAnalyzer.
        internal const string DefaultWelcomePage = "default.aspx";

        // The default master page url suffix an uncustomized team site still points at.
        private const string SeattleMasterSuffix = "_catalogs/masterpage/seattle.master";

        // The content type display form template name an uncustomized wiki home page still uses.
        private const string WikiEditForm = "WikiEditForm";

        // Web part type (short) names used to categorize a team-site home page's default web part set.
        private const string GettingStartedWebPart = "Microsoft.SharePoint.WebPartPages.GettingStartedWebPart";
        private const string SiteFeedWebPart = "Microsoft.SharePoint.Portal.WebControls.SiteFeedWebPart";
        private const string XsltListViewWebPart = "Microsoft.SharePoint.WebPartPages.XsltListViewWebPart";
        private const string ListViewWebPart = "Microsoft.SharePoint.WebPartPages.ListViewWebPart";

        // Root site home page with XsltListViewWebPart and GettingStartedWebPart (normalized form).
        // Verbatim from the legacy PageAnalyzer.DefaultRootHtml.
        private const string DefaultRootHtml = "<divclass=\"<tableid=\"layoutsTable\"style=\"width&#58;100%;\"><tbody><trstyle=\"vertical-align&#58;top;\"><tdstyle=\"width&#58;100%;padding&#58;0px;\"><divclass=\"ms-rte-layoutszone-outer\"style=\"width&#58;100%;\"><divclass=\"ms-rte-layoutszone-inner\"style=\"min-height&#58;60px;word-wrap&#58;break-word;margin&#58;0px;border&#58;0px;\"><divclass=\"ms-rtestate-readms-rte-wpbox\"><divclass=\"ms-rtestate-read\"id=\"div_\"></div><divclass=\"ms-rtestate-read\"id=\"vid_\"style=\"display&#58;none;\"></div></div><divclass=\"ms-rtestate-readms-rte-wpbox\"><divclass=\"ms-rtestate-read\"id=\"div_\"></div><divclass=\"ms-rtestate-read\"id=\"vid_\"style=\"display&#58;none;\"></div></div></div></div></td></tr></tbody></table><spanid=\"layoutsData\"style=\"display&#58;none;\">false,false,1</span></div>";

        // Home page with SiteFeedWebPart, XsltListViewWebPart and GettingStartedWebPart (normalized form).
        // Verbatim from the legacy PageAnalyzer.DefaultHtml.
        private const string DefaultHtml = "<divclass=\"<tableid=\"layoutsTable\"style=\"width&#58;100%;\"><tbody><trstyle=\"vertical-align&#58;top;\"><tdcolspan=\"2\"><divclass=\"ms-rte-layoutszone-outer\"style=\"width&#58;100%;\"><divclass=\"ms-rte-layoutszone-inner\"style=\"word-wrap&#58;break-word;margin&#58;0px;border&#58;0px;\"><divclass=\"ms-rtestate-readms-rte-wpbox\"><divclass=\"ms-rtestate-read\"id=\"div_\"></div><divclass=\"ms-rtestate-read\"id=\"vid_\"style=\"display&#58;none;\"></div></div></div></div></td></tr><trstyle=\"vertical-align&#58;top;\"><tdstyle=\"width&#58;49.95%;\"><divclass=\"ms-rte-layoutszone-outer\"style=\"width&#58;100%;\"><divclass=\"ms-rte-layoutszone-inner\"style=\"word-wrap&#58;break-word;margin&#58;0px;border&#58;0px;\"><divclass=\"ms-rtestate-readms-rte-wpbox\"><divclass=\"ms-rtestate-read\"id=\"div_\"></div><divclass=\"ms-rtestate-read\"id=\"vid_\"style=\"display&#58;none;\"></div></div></div></div></td><tdclass=\"ms-wiki-columnSpacing\"style=\"width&#58;49.95%;\"><divclass=\"ms-rte-layoutszone-outer\"style=\"width&#58;100%;\"><divclass=\"ms-rte-layoutszone-inner\"style=\"word-wrap&#58;break-word;margin&#58;0px;border&#58;0px;\"><divclass=\"ms-rtestate-readms-rte-wpbox\"><divclass=\"ms-rtestate-read\"id=\"div_\"></div><divclass=\"ms-rtestate-read\"id=\"vid_\"style=\"display&#58;none;\"></div></div></div></div></td></tr></tbody></table><spanid=\"layoutsData\"style=\"display&#58;none;\">true,false,2</span></div>";

        private static readonly Regex GuidRegex = new("([0-9A-Fa-f]{8}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{4}[-][0-9A-Fa-f]{12})", RegexOptions.Compiled);
        private static readonly Regex ExternalClassRegex = new(@"(ExternalClass.{32}"">)", RegexOptions.Compiled);

        /// <summary>
        /// Returns the welcome page name to compare against, defaulting to <c>default.aspx</c> when the
        /// web reports no welcome page (the legacy behaviour for web-part-page home pages).
        /// </summary>
        internal static string NormalizeWelcomePage(string welcomePage)
        {
            return string.IsNullOrEmpty(welcomePage) ? DefaultWelcomePage : welcomePage;
        }

        /// <summary>
        /// Whether the given page is the web's home page: a case-insensitive suffix match of the page's
        /// server-relative url against the web's welcome page. Ported from the legacy
        /// <c>pageUrl.EndsWith(homePageUrl)</c> compare.
        /// </summary>
        /// <param name="pageUrl">The page's server-relative url.</param>
        /// <param name="welcomePageUrl">The web's welcome page (<c>web.WelcomePage</c>); empty ⇒ default.aspx.</param>
        internal static bool IsHomePage(string pageUrl, string welcomePageUrl)
        {
            if (string.IsNullOrEmpty(pageUrl))
            {
                return false;
            }

            return pageUrl.EndsWith(NormalizeWelcomePage(welcomePageUrl), StringComparison.InvariantCultureIgnoreCase);
        }

        /// <summary>
        /// Determines whether a wiki page's <c>WikiField</c> HTML still matches the out-of-the-box default
        /// (i.e. the page was not customized). Ported verbatim from the legacy
        /// <c>PageAnalyzer.IsHtmlUncustomized</c>: strip whitespace, GUIDs and the per-page
        /// <c>ExternalClass</c> marker, then compare to the two captured default layouts.
        /// </summary>
        internal static bool IsHtmlUncustomized(string wikiHtml)
        {
            if (string.IsNullOrEmpty(wikiHtml))
            {
                return false;
            }

            string trimmed = wikiHtml.Replace("\r", "").Replace("\n", "").Replace("\r\n", "").Replace(" ", "").Trim();
            trimmed = GuidRegex.Replace(trimmed, "");
            trimmed = ExternalClassRegex.Replace(trimmed, "");

            return trimmed == DefaultHtml || trimmed == DefaultRootHtml;
        }

        /// <summary>
        /// Whether a team-site home page still has exactly the out-of-the-box default web part set: a single
        /// XsltListView, no other list views, at most one each of the site-feed / getting-started parts, and
        /// nothing else. Derived from the legacy <c>PageAnalyzer.GetPageWebPartInfo</c>, which produced four
        /// categories but whose only consumer (this heuristic) cared solely about the "default set" one — so
        /// it is reduced here to that single boolean rather than carrying three unused categories.
        /// </summary>
        internal static bool IsDefaultTeamSiteWebPartSet(IReadOnlyList<WebPartEntity> webParts)
        {
            int gettingStarted = 0;
            int siteFeed = 0;
            int xsltListView = 0;
            int listView = 0;
            int other = 0;

            if (webParts != null)
            {
                foreach (var webPart in webParts)
                {
                    string name = webPart.TypeShort();
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    switch (name)
                    {
                        case GettingStartedWebPart:
                            gettingStarted++;
                            break;
                        case SiteFeedWebPart:
                            siteFeed++;
                            break;
                        case XsltListViewWebPart:
                            xsltListView++;
                            break;
                        case ListViewWebPart:
                            listView++;
                            break;
                        default:
                            other++;
                            break;
                    }
                }
            }

            return other == 0 && siteFeed <= 1 && gettingStarted <= 1 && xsltListView == 1 && listView == 0;
        }

        /// <summary>
        /// The legacy fallback used to decide whether a home page is uncustomized when the CSOM
        /// <c>web.CanModernizeHomepage</c> API is unavailable. Ported as a pure function from the
        /// fallback branch of <c>PageAnalyzer.Analyze</c>: only a still-default <c>STS#0</c> team site home
        /// page (no publishing, not groupified, not opted out, still on seattle.master, still named the
        /// localized default home page, still the default wiki HTML, still the default web part set, still
        /// the WikiEditForm content type) is considered uncustomized.
        /// </summary>
        /// <param name="isHomePage">Whether this page is the web's home page (see <see cref="IsHomePage"/>).</param>
        /// <param name="webTemplate">The web template (e.g. <c>STS</c>).</param>
        /// <param name="webConfiguration">The web template configuration (0 for a team site).</param>
        /// <param name="publishingSiteFeatureEnabled">Whether the site publishing feature is enabled.</param>
        /// <param name="publishingWebFeatureEnabled">Whether the web publishing feature is enabled.</param>
        /// <param name="homePageModernizationOptedOut">Whether the home-page modernization opt-out web feature (<c>F478D140-…</c>) is present.</param>
        /// <param name="siteWasGroupified">Whether the group-connected ("groupified") web feature (<c>E3DC7334-…</c>) is present.</param>
        /// <param name="masterUrl">The web's master page url.</param>
        /// <param name="pageName">The page's leaf name (FileLeafRef).</param>
        /// <param name="localizedHomePageName">The localized default home page name (e.g. <c>Home.aspx</c>).</param>
        /// <param name="wikiHtml">The page's <c>WikiField</c> HTML.</param>
        /// <param name="webParts">The page's extracted web part inventory.</param>
        /// <param name="contentTypeDisplayFormTemplateName">The page content type's display form template name.</param>
        internal static bool IsUncustomizedHomePageFallback(
            bool isHomePage,
            string webTemplate,
            int webConfiguration,
            bool publishingSiteFeatureEnabled,
            bool publishingWebFeatureEnabled,
            bool homePageModernizationOptedOut,
            bool siteWasGroupified,
            string masterUrl,
            string pageName,
            string localizedHomePageName,
            string wikiHtml,
            IReadOnlyList<WebPartEntity> webParts,
            string contentTypeDisplayFormTemplateName)
        {
            // Only a default STS#0 team site home page is a candidate.
            if (!isHomePage || webTemplate != "STS" || webConfiguration != 0)
            {
                return false;
            }

            // Publishing or an explicit opt-out means the home page is not a default modern-ready one.
            if (homePageModernizationOptedOut || publishingSiteFeatureEnabled || publishingWebFeatureEnabled)
            {
                return false;
            }

            // A group-connected site does not have the classic default home page.
            if (siteWasGroupified)
            {
                return false;
            }

            // Still pointing at the default master page.
            if (string.IsNullOrEmpty(masterUrl) || !masterUrl.EndsWith(SeattleMasterSuffix, StringComparison.InvariantCultureIgnoreCase))
            {
                return false;
            }

            // Only the default home page name (Home.aspx or its localized equivalent) counts.
            if (string.IsNullOrEmpty(pageName) || !pageName.Equals(localizedHomePageName, StringComparison.InvariantCultureIgnoreCase))
            {
                return false;
            }

            // The wiki HTML must still be the out-of-the-box default.
            if (!IsHtmlUncustomized(wikiHtml))
            {
                return false;
            }

            // The web part set must still be the default team site set.
            if (!IsDefaultTeamSiteWebPartSet(webParts))
            {
                return false;
            }

            // And the content type must still be the default wiki edit form.
            return string.Equals(contentTypeDisplayFormTemplateName, WikiEditForm, StringComparison.InvariantCulture);
        }
    }
}
