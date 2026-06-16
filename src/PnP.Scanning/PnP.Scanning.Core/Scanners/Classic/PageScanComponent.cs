using System.Text.RegularExpressions;
using Microsoft.SharePoint.Client;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Scanners.WebPartMapping;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal static class PageScanComponent
    {
        private static readonly Guid FeatureId_Site_Publishing = new("F6924D36-2FA8-4F0B-B16D-06B7250180FA");
        private static readonly Guid FeatureId_Web_Publishing = new("94C94CA6-B32F-4DA9-A9E3-1F3D343D7ECB");
        private static readonly Guid FeatureId_Web_ModernPage = new("B6917CB1-93A0-4B97-A84D-7CF49975D4EC");

        // Home-page modernization opt-out web feature and the group-connected ("groupified") web feature.
        // Resolved here (not in the pure HomePageDetector) because they are part of the per-web CSOM wiring.
        // Ported from the legacy PageAnalyzer.
        private static readonly Guid FeatureId_Web_HomePageModernizationOptOut = new("F478D140-B148-4038-9CB0-84A8F1E4BE09");
        private static readonly Guid FeatureId_Web_GroupHomePage = new("E3DC7334-CEC0-4D2C-8B90-E4857698FC4E");

        // The web part mapping model (embedded webpartmapping.xml) is read-only after construction, so a
        // single shared instance is reused across webs/threads instead of re-parsing the ~1370-line file
        // for every web. Also reused by the post-scan unique-web-part rollup (StorageManager.
        // PopulateWebPartUniqueAsync) so it shares the same parsed mapping file.
        internal static readonly WebPartMappingManager MappingManager = new();

        // Fields
        private const string FileRefField = "FileRef";
        private const string FileLeafRefField = "FileLeafRef";
        private const string HtmlFileTypeField = "HTML_x0020_File_x0020_Type";
        private const string FileTypeField = "File_x0020_Type";
        private const string ContentTypeIdField = "ContentTypeId";
        private const string WikiField = "WikiField";
        private const string ModifiedField = "Modified";
        private const string ModifiedByField = "Editor";
        private const string CreatedField = "Created";
        private const string ClientSideApplicationIdField = "ClientSideApplicationId";
        private const string TitleField = "Title";
        private const string BSNField = "BSN";

        // Page Types
        internal const string ModernPage = "ModernPage";
        internal const string WebPartPage = "WebPartPage";
        internal const string WikiPage = "WikiPage";
        internal const string ASPXPage = "ASPXPage";
        internal const string PublishingPage = "PublishingPage";
        internal const string BlogPage = "BlogPage";
        internal const string DelveBlogPage = "DelveBlogPage";

        // File type value identifying a Delve blog page (point publishing)
        private const string DelveBlogFileType = "pointpub";

        // The localized default home page resource (e.g. "Home") used to recognize an uncustomized STS#0 home page.
        private const string WikiHomePageResource = "$Resources:WikiPageHomePageName";

        internal static async Task ExecuteAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext)
        {
            var options = ((ClassicScanner)scannerBase).Options;

            List<ClassicPage> pagesList = new();
            List<PageEnrichmentInput> enrichmentInputs = new();
            HashSet<string> remediationCodes = new();

            bool sitePublishingEnabled = FeatureEnabled(context.Site.Features, FeatureId_Site_Publishing);
            bool webPublishingEnabled = FeatureEnabled(context.Web.Features, FeatureId_Web_Publishing);

            // The web's welcome page drives the HomePage flag and the optional HomePageOnly filter.
            string welcomePage = await GetWelcomePageAsync(csomContext).ConfigureAwait(false);

            var discovery = new PageDiscovery
            {
                ScannerBase = scannerBase,
                WelcomePage = welcomePage,
                HomePageOnly = options.HomePageOnly,
                SkipUserInformation = options.SkipUserInformation,
                Pages = pagesList,
                RemediationCodes = remediationCodes,
                EnrichmentInputs = enrichmentInputs,
            };

            var lists = ScannerBase.CleanLoadedLists(context);

            if (scannerBase.WebTemplate == "BLOG#0")
            {
                // Load the blog pages library
                var blogList = lists.FirstOrDefault(l => l.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.Posts);
                if (blogList != null)
                {
                    // Load the blog pages
                    await QueryListAsync(blogList, PageQuery(new List<string> { }, filterOnASPXPages: false), (IEnumerable<IListItem> listItems) =>
                    {
                        foreach (var listItem in listItems)
                        {
                            AddBlogPage(discovery, blogList, listItem);
                        }
                    }).ConfigureAwait(false);
                }
            }
            else if ((scannerBase.WebTemplate == "BLANKINTERNET#0" || scannerBase.WebTemplate == "ENTERWIKI#0" ||
                      scannerBase.WebTemplate == "SRCHCEN#0" || scannerBase.WebTemplate == "CMSPUBLISHING#0") &&
                     sitePublishingEnabled && webPublishingEnabled)
            {
                await QueryPublishingPagesAsync(discovery, lists).ConfigureAwait(false);
            }
            else
            {
                // A team site can also have the publishing features enabled
                if (sitePublishingEnabled && webPublishingEnabled)
                {
                    await QueryPublishingPagesAsync(discovery, lists).ConfigureAwait(false);
                }

                // Check for the regular pages library
                var sitePagesLibraries = lists.Where(l => l.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.WebPageLibrary);
                if (sitePagesLibraries.Any())
                {
                    // Load regular pages
                    foreach (var sitePagesLibrary in sitePagesLibraries)
                    {
                        await QueryListAsync(sitePagesLibrary, PageQuery(new List<string> { HtmlFileTypeField, WikiField, ClientSideApplicationIdField }), (IEnumerable<IListItem> listItems) =>
                        {
                            foreach (var listItem in listItems)
                            {
                                AddSitePage(discovery, sitePagesLibrary, listItem);
                            }
                        }).ConfigureAwait(false);
                    }
                }
            }

            // Enrich the discovered classic pages with their web part inventory, mapping readiness, page
            // layout and (for the home page) the uncustomized-home-page verdict. Only web part / wiki /
            // publishing pages carry a transformable web part surface, so only those were captured for
            // enrichment. Per-page failures are logged and skipped — a single bad page must not fail the web.
            List<ClassicPageWebPart> webPartsList = new();
            foreach (var input in enrichmentInputs)
            {
                try
                {
                    var pageWebParts = await ExtractWebPartsAsync(csomContext, input, options.ExportWebPartProperties).ConfigureAwait(false);

                    // Stamps IsMappable per web part + WebPartCount / MappingPercentage / UnmappedWebParts on the page.
                    PageMappingCalculator.ApplyMapping(input.Page, pageWebParts, MappingManager);

                    webPartsList.AddRange(pageWebParts);

                    if (input.Page.HomePage)
                    {
                        input.Page.UncustomizedHomePage = await DetermineUncustomizedHomePageAsync(
                            scannerBase, context, csomContext, input, pageWebParts,
                            sitePublishingEnabled, webPublishingEnabled).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    scannerBase.Logger.Warning(ex, "Failed to assess the web parts of classic page {PageUrl}; skipping its inventory", input.Page.PageUrl);
                }
            }

            // Page usage statistics (recent / lifetime views + unique users) via a per-page search lookup,
            // unless the caller opted out. Applies to every classic page, not only those carrying a web
            // part inventory. The per-page lookup (RowLimit 1) deliberately avoids the legacy bulk-search
            // IndexDocId paging risk. Per-page failures are logged and skipped (view counts stay zero).
            if (!options.SkipUsageInformation)
            {
                foreach (var page in pagesList)
                {
                    try
                    {
                        string searchPath = PageUsageAnalyzer.BuildPageSearchPath(scannerBase.SiteUrl, scannerBase.WebUrl, page.PageUrl, page.HomePage);
                        var usageRow = await PageUsageAnalyzer.QueryPageUsageAsync(csomContext, searchPath).ConfigureAwait(false);
                        PageUsageAnalyzer.ApplyUsage(page, usageRow, options.SkipUsageInformation);
                    }
                    catch (Exception ex)
                    {
                        scannerBase.Logger.Warning(ex, "Failed to retrieve usage statistics for classic page {PageUrl}; leaving view counts at zero", page.PageUrl);
                    }
                }
            }

            if (pagesList.Count > 0)
            {
                await scannerBase.StorageManager.StorePageInformationAsync(scannerBase.ScanId, pagesList);
            }

            if (webPartsList.Count > 0)
            {
                await scannerBase.StorageManager.StorePageWebPartsAsync(scannerBase.ScanId, webPartsList);
            }

            // Loop over the found pages and save the page summary information
            int wikiPageCounter = 0;
            int blogPageCounter = 0;
            int webPartPageCounter = 0;
            int aspxPageCounter = 0;
            int publishingPageCounter = 0;

            foreach (var page in pagesList)
            {
                switch (page.PageType)
                {
                    case WikiPage:
                        wikiPageCounter++;
                        break;
                    case BlogPage:
                        blogPageCounter++;
                        break;
                    case WebPartPage:
                        webPartPageCounter++;
                        break;
                    case ASPXPage:
                        aspxPageCounter++;
                        break;
                    case PublishingPage:
                        publishingPageCounter++;
                        break;
                }
            }

            await scannerBase.StorageManager.StorePageSummaryAsync(scannerBase.ScanId, scannerBase.SiteUrl, scannerBase.WebUrl, scannerBase.WebTemplate, context, remediationCodes,
                                                                   discovery.ModernPageCounter, wikiPageCounter, blogPageCounter, webPartPageCounter, aspxPageCounter, publishingPageCounter);
        }

        private static void AddBlogPage(PageDiscovery disc, IList blogList, IListItem listItem)
        {
            string pageUrl = GetFieldValue(listItem, FileRefField, $"{listItem.Id}");

            if (disc.HomePageOnly && !HomePageDetector.IsHomePage(pageUrl, disc.WelcomePage))
            {
                return;
            }

            disc.Pages.Add(new ClassicPage
            {
                ScanId = disc.ScannerBase.ScanId,
                SiteUrl = disc.ScannerBase.SiteUrl,
                WebUrl = disc.ScannerBase.WebUrl,
                PageUrl = pageUrl,
                PageName = GetFieldValue(listItem, TitleField, ""),
                ListUrl = blogList.RootFolder.ServerRelativeUrl,
                ListTitle = blogList.Title,
                ListId = blogList.Id,
                ModifiedAt = GetFieldValue<DateTime>(listItem, ModifiedField),
                ModifiedBy = GetModifiedBy(listItem.Values, disc.SkipUserInformation),
                PageType = BlogPage,
                HomePage = HomePageDetector.IsHomePage(pageUrl, disc.WelcomePage),
                RemediationCode = RemediationCodes.CP4.ToString(),
            });

            disc.RemediationCodes.Add(RemediationCodes.CP4.ToString());
        }

        private static void AddSitePage(PageDiscovery disc, IList sitePagesLibrary, IListItem listItem)
        {
            string pageUrl = GetFieldValue(listItem, FileRefField, $"{listItem.Id}");

            if (disc.HomePageOnly && !HomePageDetector.IsHomePage(pageUrl, disc.WelcomePage))
            {
                return;
            }

            var pageToAdd = new ClassicPage
            {
                ScanId = disc.ScannerBase.ScanId,
                SiteUrl = disc.ScannerBase.SiteUrl,
                WebUrl = disc.ScannerBase.WebUrl,
                PageUrl = pageUrl,
                PageName = GetFieldValue(listItem, TitleField, "") != "" ? GetFieldValue(listItem, TitleField, "") : Path.GetFileNameWithoutExtension(pageUrl),
                ListUrl = sitePagesLibrary.RootFolder.ServerRelativeUrl,
                ListTitle = sitePagesLibrary.Title,
                ListId = sitePagesLibrary.Id,
                ModifiedAt = GetFieldValue<DateTime>(listItem, ModifiedField),
                ModifiedBy = GetModifiedBy(listItem.Values, disc.SkipUserInformation),
                PageType = GetPageType(listItem),
                HomePage = HomePageDetector.IsHomePage(pageUrl, disc.WelcomePage),
            };

            switch (pageToAdd.PageType)
            {
                case WikiPage:
                    pageToAdd.RemediationCode = RemediationCodes.CP2.ToString();
                    disc.RemediationCodes.Add(RemediationCodes.CP2.ToString());
                    break;
                case WebPartPage:
                    pageToAdd.RemediationCode = RemediationCodes.CP1.ToString();
                    disc.RemediationCodes.Add(RemediationCodes.CP1.ToString());
                    break;
                case ASPXPage:
                    pageToAdd.RemediationCode = RemediationCodes.CP5.ToString();
                    disc.RemediationCodes.Add(RemediationCodes.CP5.ToString());
                    break;
            }

            if (pageToAdd.AddToDatabase())
            {
                disc.Pages.Add(pageToAdd);

                // Wiki and web part pages carry a web part inventory we can extract + map.
                if (pageToAdd.PageType == WikiPage || pageToAdd.PageType == WebPartPage)
                {
                    disc.EnrichmentInputs.Add(new PageEnrichmentInput
                    {
                        Page = pageToAdd,
                        WikiFieldHtml = GetFieldValue<string>(listItem, WikiField),
                        FileLeafRef = GetFieldValue(listItem, FileLeafRefField, ""),
                    });
                }
            }
            else
            {
                disc.ModernPageCounter++;
            }
        }

        private static async Task QueryPublishingPagesAsync(PageDiscovery disc, List<IList> lists)
        {
            var pagesLibrary = lists.FirstOrDefault(l => l.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.PublishingPagesLibrary);
            if (pagesLibrary != null)
            {
                // Load publishing pages, this is a publishing portal
                await QueryListAsync(pagesLibrary, PageQuery(new List<string> { }), (IEnumerable<IListItem> listItems) =>
                {
                    foreach (var listItem in listItems)
                    {
                        AddPublishingPage(disc, pagesLibrary, listItem);
                    }
                }).ConfigureAwait(false);
            }
        }

        private static void AddPublishingPage(PageDiscovery disc, IList pagesLibrary, IListItem listItem)
        {
            string pageUrl = GetFieldValue(listItem, FileRefField, $"{listItem.Id}");

            if (disc.HomePageOnly && !HomePageDetector.IsHomePage(pageUrl, disc.WelcomePage))
            {
                return;
            }

            var pageToAdd = new ClassicPage
            {
                ScanId = disc.ScannerBase.ScanId,
                SiteUrl = disc.ScannerBase.SiteUrl,
                WebUrl = disc.ScannerBase.WebUrl,
                PageUrl = pageUrl,
                PageName = GetFieldValue(listItem, TitleField, "") != "" ? GetFieldValue(listItem, TitleField, "") : Path.GetFileNameWithoutExtension(pageUrl),
                ListUrl = pagesLibrary.RootFolder.ServerRelativeUrl,
                ListTitle = pagesLibrary.Title,
                ListId = pagesLibrary.Id,
                ModifiedAt = GetFieldValue<DateTime>(listItem, ModifiedField),
                ModifiedBy = GetModifiedBy(listItem.Values, disc.SkipUserInformation),
                PageType = PublishingPage,
                HomePage = HomePageDetector.IsHomePage(pageUrl, disc.WelcomePage),
                RemediationCode = RemediationCodes.CP3.ToString(),
            };

            disc.Pages.Add(pageToAdd);
            // CP3 = Publishing page (matches pageToAdd.RemediationCode). Pre-T8 this added CP4 (Blog page),
            // a copy-paste quirk that mislabeled a publishing web's aggregated remediation codes.
            disc.RemediationCodes.Add(RemediationCodes.CP3.ToString());

            disc.EnrichmentInputs.Add(new PageEnrichmentInput
            {
                Page = pageToAdd,
                WikiFieldHtml = null,
                FileLeafRef = GetFieldValue(listItem, FileLeafRefField, ""),
            });
        }

        // Dispatches a discovered page to the right web part extractor. Web part / wiki / publishing
        // pages each have a dedicated CSOM extraction path; anything else yields no web parts.
        private static async Task<List<ClassicPageWebPart>> ExtractWebPartsAsync(ClientContext csomContext, PageEnrichmentInput input, bool exportWebPartProperties)
        {
            switch (input.Page.PageType)
            {
                case WebPartPage:
                    return await PageWebPartExtractor.ExtractFromWebPartPageAsync(csomContext, input.Page, exportWebPartProperties).ConfigureAwait(false);
                case WikiPage:
                    return await PageWebPartExtractor.ExtractFromWikiPageAsync(csomContext, input.Page, input.WikiFieldHtml, exportWebPartProperties).ConfigureAwait(false);
                case PublishingPage:
                    return await PageWebPartExtractor.ExtractFromPublishingPageAsync(csomContext, input.Page, exportWebPartProperties).ConfigureAwait(false);
                default:
                    return new List<ClassicPageWebPart>();
            }
        }

        // Determines whether the web's home page is a still-default ("uncustomized") home page. The
        // reliable answer comes from the CanModernizeHomepage CSOM API (a bare property read with no logic
        // to port); when that API is unavailable we fall back to the legacy HTML/web-part heuristic, whose
        // pure decision lives in HomePageDetector. This per-web CSOM wiring is the T8 hand-off from T10.
        private static async Task<bool> DetermineUncustomizedHomePageAsync(
            ScannerBase scannerBase, PnPContext context, ClientContext csomContext,
            PageEnrichmentInput input, List<ClassicPageWebPart> pageWebParts,
            bool publishingSiteFeatureEnabled, bool publishingWebFeatureEnabled)
        {
            // Primary path: the CanModernizeHomepage CSOM API.
            try
            {
                var canModernizeHomepage = csomContext.Web.CanModernizeHomepage;
                csomContext.Load(canModernizeHomepage);
                await csomContext.ExecuteQueryAsync().ConfigureAwait(false);

                return canModernizeHomepage.CanModernizeHomepage;
            }
            catch (Exception ex)
            {
                scannerBase.Logger.Information(ex, "CanModernizeHomepage API unavailable for {PageUrl}; falling back to the HTML heuristic", input.Page.PageUrl);
            }

            // Fallback path: gather the inputs the legacy heuristic needs and let HomePageDetector decide.
            try
            {
                var (template, configuration) = SplitWebTemplate(scannerBase.WebTemplate);

                bool homePageModernizationOptedOut = FeatureEnabled(context.Web.Features, FeatureId_Web_HomePageModernizationOptOut);
                bool siteWasGroupified = FeatureEnabled(context.Web.Features, FeatureId_Web_GroupHomePage);

                var web = csomContext.Web;
                csomContext.Load(web, w => w.MasterUrl, w => w.Language);
                var listItem = web.GetFileByServerRelativeUrl(input.Page.PageUrl).ListItemAllFields;
                csomContext.Load(listItem.ContentType, ct => ct.DisplayFormTemplateName);
                await csomContext.ExecuteQueryAsync().ConfigureAwait(false);

                string localizedHomePageName = await GetLocalizedHomePageNameAsync(csomContext, (int)web.Language).ConfigureAwait(false);

                // HomePageDetector works over WebPartEntity; project the short type from the extracted rows.
                var entities = pageWebParts.Select(wp => new WebPartEntity { Type = wp.WebPartType }).ToList();

                return HomePageDetector.IsUncustomizedHomePageFallback(
                    isHomePage: true,
                    webTemplate: template,
                    webConfiguration: configuration,
                    publishingSiteFeatureEnabled: publishingSiteFeatureEnabled,
                    publishingWebFeatureEnabled: publishingWebFeatureEnabled,
                    homePageModernizationOptedOut: homePageModernizationOptedOut,
                    siteWasGroupified: siteWasGroupified,
                    masterUrl: web.MasterUrl,
                    pageName: input.FileLeafRef,
                    localizedHomePageName: localizedHomePageName,
                    wikiHtml: input.WikiFieldHtml,
                    webParts: entities,
                    contentTypeDisplayFormTemplateName: listItem.ContentType.DisplayFormTemplateName);
            }
            catch (Exception ex)
            {
                scannerBase.Logger.Warning(ex, "Uncustomized home page fallback failed for {PageUrl}", input.Page.PageUrl);
                return false;
            }
        }

        // Resolves the localized default home page name (e.g. "Home.aspx"). Ported from the legacy
        // PageAnalyzer: strip the quote-like characters the resource may carry and append ".aspx".
        private static async Task<string> GetLocalizedHomePageNameAsync(ClientContext csomContext, int language)
        {
            var result = Microsoft.SharePoint.Client.Utilities.Utility.GetLocalizedString(csomContext, WikiHomePageResource, "core", language);
            await csomContext.ExecuteQueryAsync().ConfigureAwait(false);

            return $"{Regex.Replace(result.Value ?? "", @"['´`]", "")}.aspx";
        }

        // Reads the web's welcome page (server-relative-from-web), used for the HomePage flag and the
        // HomePageOnly filter. Returns an empty string when the property cannot be read so discovery
        // continues (HomePageDetector defaults the empty welcome page to default.aspx).
        private static async Task<string> GetWelcomePageAsync(ClientContext csomContext)
        {
            try
            {
                var rootFolder = csomContext.Web.RootFolder;
                csomContext.Load(rootFolder, f => f.WelcomePage);
                await csomContext.ExecuteQueryAsync().ConfigureAwait(false);

                return rootFolder.WelcomePage ?? "";
            }
            catch
            {
                return "";
            }
        }

        // Splits a web template like "STS#0" into its template ("STS") and configuration (0). An
        // unparseable configuration yields -1 so it never accidentally matches the default home page (0).
        private static (string template, int configuration) SplitWebTemplate(string webTemplate)
        {
            if (string.IsNullOrEmpty(webTemplate))
            {
                return ("", -1);
            }

            var parts = webTemplate.Split('#');
            int configuration = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : -1;
            return (parts[0], configuration);
        }

        private static async Task QueryListAsync(IList list, string viewXml, Action<IEnumerable<IListItem>> processResults)
        {
            bool paging = true;
            string nextPage = null;
            while (paging)
            {
                // Clear the previous page (if any)
                list.Items.Clear();

                // Execute the query, this populates a page of list items
                var output = await list.LoadListDataAsStreamAsync(new PnP.Core.Model.SharePoint.RenderListDataOptions()
                {
                    ViewXml = viewXml,
                    RenderOptions = RenderListDataOptionsFlags.ListData,
                    Paging = nextPage ?? null,
                }).ConfigureAwait(false);

                if (output.ContainsKey("NextHref"))
                {
                    nextPage = output["NextHref"].ToString().Substring(1);
                }
                else
                {
                    paging = false;
                }

                processResults?.Invoke(list.Items.AsRequested());
            }
        }

        private static string PageQuery(List<string> extraFields, bool filterOnASPXPages = true)
        {
            string extraViewFields = "";
            string filter = "";

            if (extraFields.Count > 0)
            {
                foreach(var field in extraFields)
                {
                    extraViewFields = $"{extraViewFields}<FieldRef Name='{field}' />";
                }
            }

            if (filterOnASPXPages)
            {
                filter = $@"
                          <Query>
                            <Where>
                              <Contains>
                                <FieldRef Name='File_x0020_Type'/>
                                <Value Type='text'>aspx</Value>
                              </Contains>
                            </Where>
                          </Query>";
            }

            return $@"
                <View Scope='RecursiveAll'>
                  <ViewFields>
                    <FieldRef Name='{ContentTypeIdField}' />
                    <FieldRef Name='{FileRefField}' />
                    <FieldRef Name='{FileLeafRefField}' />
                    <FieldRef Name='{FileTypeField}' />
                    <FieldRef Name='{ModifiedField}' />
                    <FieldRef Name='{ModifiedByField}' />
                    <FieldRef Name='{CreatedField}' />
                    <FieldRef Name='{TitleField}' />
                    <FieldRef Name='{BSNField}' />
                    {extraViewFields}
                  </ViewFields>
                  {filter}
                  <OrderBy Override='TRUE'><FieldRef Name= 'ID' Ascending= 'FALSE' /></OrderBy>
                  <RowLimit Paged='TRUE'>1000</RowLimit>
                </View>";
        }

        private static bool FeatureEnabled(IFeatureCollection features, Guid feature)
        {
            return features.AsRequested().FirstOrDefault(f => f.DefinitionId == feature) != null;
        }

        private static T GetFieldValue<T>(IListItem listItem, string fieldName, T defaultValue = default)
        {
            return GetFieldValue(listItem.Values, fieldName, defaultValue);
        }

        private static T GetFieldValue<T>(IDictionary<string, object> fieldValues, string fieldName, T defaultValue = default)
        {
            if (fieldValues.ContainsKey(fieldName) && fieldValues[fieldName] != null)
            {
                return (T)fieldValues[fieldName];
            }

            return defaultValue;
        }

        private static string GetPageType(IListItem listItem)
        {
            return GetPageType(listItem.Values);
        }

        // Pure classification over the raw field values (no CSOM) so it is unit-testable.
        internal static string GetPageType(IDictionary<string, object> fieldValues)
        {
            if (GetFieldValue(fieldValues, HtmlFileTypeField, string.Empty) == "SharePoint.WebPartPage.Document")
            {
                return WebPartPage;
            }

            if (GetFieldValue(fieldValues, ClientSideApplicationIdField, string.Empty).Equals($"{{{FeatureId_Web_ModernPage}}}", StringComparison.InvariantCultureIgnoreCase))
            {
                return ModernPage;
            }

            if (GetFieldValue<string>(fieldValues, WikiField) != null)
            {
                return WikiPage;
            }

            if (GetFieldValue(fieldValues, FileTypeField, string.Empty).Equals(DelveBlogFileType, StringComparison.InvariantCultureIgnoreCase))
            {
                return DelveBlogPage;
            }

            if (GetFieldValue<string>(fieldValues, BSNField) != "")
            {
                return ASPXPage;
            }
            else
            {
                return WikiPage;
            }
        }

        // Pure extraction of the page "Modified By" (no CSOM) so it is unit-testable.
        // Parity with the legacy scanner's ListItemExtensions.LastModifiedBy: prefer the account
        // email, falling back to the lookup display value when no email is present.
        internal static string GetModifiedBy(IDictionary<string, object> fieldValues, bool skipUserInformation)
        {
            if (skipUserInformation)
            {
                return null;
            }

            if (fieldValues.TryGetValue(ModifiedByField, out object value) && value is IFieldUserValue user)
            {
                return !string.IsNullOrEmpty(user.Email) ? user.Email : user.LookupValue;
            }

            return null;
        }

        // Cross-cutting state threaded through the per-web page discovery branches.
        private sealed class PageDiscovery
        {
            public ScannerBase ScannerBase { get; init; }

            public string WelcomePage { get; init; }

            public bool HomePageOnly { get; init; }

            public bool SkipUserInformation { get; init; }

            public List<ClassicPage> Pages { get; init; }

            public HashSet<string> RemediationCodes { get; init; }

            public List<PageEnrichmentInput> EnrichmentInputs { get; init; }

            // Modern pages are discovered but not persisted; they are only counted for the summary.
            public int ModernPageCounter { get; set; }
        }

        // A discovered page plus the discovery-time field values the enrichment step needs (the wiki HTML
        // and the page leaf name), captured because the live list items are released as discovery pages.
        private sealed class PageEnrichmentInput
        {
            public ClassicPage Page { get; init; }

            public string WikiFieldHtml { get; init; }

            public string FileLeafRef { get; init; }
        }
    }
}
