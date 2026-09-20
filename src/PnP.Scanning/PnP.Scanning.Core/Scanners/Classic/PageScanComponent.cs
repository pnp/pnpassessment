using System.Text.RegularExpressions;
using Microsoft.SharePoint.Client;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Scanners.WebPartMapping;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Discovery;

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

        internal static async Task ExecuteAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext,
            IReadOnlyList<ClassicPageDiscovery> discoveredPages)
        {
            var options = ((ClassicScanner)scannerBase).Options;

            List<ClassicPage> pagesList = new();
            List<PageEnrichmentInput> enrichmentInputs = new();
            HashSet<string> remediationCodes = new();

            bool sitePublishingEnabled = FeatureEnabled(context.Site.Features, FeatureId_Site_Publishing);
            bool webPublishingEnabled = FeatureEnabled(context.Web.Features, FeatureId_Web_Publishing);

            // The web's welcome page drives the HomePage flag and the optional HomePageOnly filter.
            var (welcomePage, welcomePageKnown) = await GetWelcomePageAsync(csomContext).ConfigureAwait(false);

            var discovery = new PageDiscovery
            {
                ScannerBase = scannerBase,
                WelcomePage = welcomePage,
                WelcomePageKnown = welcomePageKnown,
                HomePageOnly = options.HomePageOnly,
                SkipUserInformation = options.SkipUserInformation,
                Pages = pagesList,
                RemediationCodes = remediationCodes,
                EnrichmentInputs = enrichmentInputs,
            };

            var lists = context.Web.Lists.AsRequested().ToList();
            var discoveryWriter = new AssessmentDiscoveryWriter(scannerBase.ScanId);

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
            // The lossless provider has already found the physical pages. Do not rediscover
            // only selected libraries or gate their existence on publishing features/template.
            foreach (var row in discoveredPages)
            {
                // The discovery provider records a nullable value so an unavailable modeled call is
                // not mistaken for false. Prefer the later CSOM read when it succeeded; it is also the
                // value used by the page assessment and keeps --homepageonly from filtering every page
                // merely because the discovery-specific WelcomePage read failed.
                row.HomePage = ResolveHomePageState(row.Url, welcomePage, welcomePageKnown, row.HomePage);
                if (options.HomePageOnly && row.HomePage != true)
                {
                    row.AssessmentStatus = "NotSelected";
                    await discoveryWriter.WriteAsync(new[] { row }).ConfigureAwait(false);
                    continue;
                }
                var list = lists.FirstOrDefault(value => value.Id == row.ListId);
                if (list == null || row.ListItemId == null)
                {
                    // Forms/Views and root files may not have a list item. Their physical
                    // discovery is still valid; do not pretend that zero extracted WPs is a pass.
                    row.AssessmentStatus = "NotApplicable";
                    await discoveryWriter.WriteAsync(new[] { row }).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    var input = await LoadPhysicalPageAsync(list, row, welcomePage,
                        options.SkipUserInformation, welcomePageKnown).ConfigureAwait(false);
                    row.PageType = input.Page.PageType;
                    row.HomePage = input.Page.HomePage;
                    if (row.PageType == PublishingPage) AddPublishingPage(discovery, input);
                    else AddSitePage(discovery, input);
                    row.AssessmentStatus = row.PageType == ModernPage ? "NotApplicable" : "Complete";
                }
                catch (Exception ex)
                {
                    if (scannerBase.ScanManager.GetCancellationTokenSource(scannerBase.ScanId).IsCancellationRequested)
                    {
                        throw;
                    }
                    row.AssessmentStatus = "Failed";
                    AssessmentWebDiscovery.AddError(row, "PageMetadata", AssessmentWebDiscovery.ErrorCode(ex),
                        AssessmentWebDiscovery.ErrorDetail(ex));
                    scannerBase.Logger.Warning(ex, "Page metadata assessment failed for {PageUrl}; retaining discovery", row.Url);
                }
                await discoveryWriter.WriteAsync(new[] { row }).ConfigureAwait(false);
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

                    if (input.Page.HomePage == true)
                    {
                        input.Page.UncustomizedHomePage = await DetermineUncustomizedHomePageAsync(
                            scannerBase, context, csomContext, input, pageWebParts,
                            sitePublishingEnabled, webPublishingEnabled).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    if (scannerBase.ScanManager.GetCancellationTokenSource(scannerBase.ScanId).IsCancellationRequested) throw;
                    var discovered = discoveredPages.FirstOrDefault(row =>
                        string.Equals(row.Url, input.Page.PageUrl, StringComparison.OrdinalIgnoreCase));
                    if (discovered != null)
                    {
                        discovered.AssessmentStatus = "Failed";
                        AssessmentWebDiscovery.AddError(discovered, "WebPartAssessment", AssessmentWebDiscovery.ErrorCode(ex),
                            AssessmentWebDiscovery.ErrorDetail(ex));
                        await discoveryWriter.WriteAsync(new[] { discovered }).ConfigureAwait(false);
                    }
                    scannerBase.Logger.Warning(ex, "Failed to assess the web parts of classic page {PageUrl}; skipping its inventory", input.Page.PageUrl);
                }
            }

            // SharePoint Search usage (ViewsRecent / ViewsLifeTime) is intentionally skipped for Classic
            // pages: Search Analytics does not reliably populate these managed properties for classic .aspx
            // pages, producing all-zero results. Audit log usage (ClassicPageViewed / ClassicPageCreated /
            // ClassicPageEdited) collected in PostScanningAsync is the authoritative source instead.

            if (pagesList.Count > 0)
            {
                foreach (var page in pagesList)
                {
                    var found = discoveredPages.FirstOrDefault(row => string.Equals(row.Url, page.PageUrl, StringComparison.OrdinalIgnoreCase));
                    if (found == null) continue; // Blog posts are list items, not physical ASPX files.
                    ApplyDiscoveryState(page, found);
                }
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
            var homePage = ResolveHomePageState(pageUrl, disc.WelcomePage, disc.WelcomePageKnown, null);

            if (disc.HomePageOnly && homePage != true)
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
                HomePage = homePage,
                RemediationCode = RemediationCodes.CP4.ToString(),
            });

            disc.RemediationCodes.Add(RemediationCodes.CP4.ToString());
        }

        // Kept independent of ScannerBase so the same SDK field selection and metadata projection
        // can be replayed offline against the native database/report pipeline.
        internal static async Task<PageEnrichmentInput> LoadPhysicalPageAsync(IList list, ClassicPageDiscovery row,
            string welcomePage, bool skipUserInformation, bool? welcomePageKnown = null)
        {
            if (row.ListId != list.Id || row.ListItemId == null || row.ListItemId <= 0)
                throw new InvalidDataException("Physical page metadata requires its discovered list and list-item identity.");

            // REST $select=* (IListItem.All) still omits computed fields such as FileRef,
            // HTML_x0020_File_x0020_Type and ClientSideApplicationId. Reuse the native
            // list-stream reader with explicit ViewFields, restricted to this discovered ID.
            // Unlike selecting optional REST properties, missing ViewFields are omitted by
            // SharePoint rather than failing classic libraries that lack a modern-only field.
            IListItem item = null;
            await QueryListAsync(list, PageQuery(new List<string> { WikiField, HtmlFileTypeField, ClientSideApplicationIdField },
                filterOnASPXPages: false, itemId: row.ListItemId.Value, skipUserInformation: skipUserInformation), items =>
            {
                foreach (var candidate in items)
                {
                    if (candidate.Id != row.ListItemId.Value || item != null)
                        throw new InvalidDataException("The requested physical page list item was not returned uniquely.");
                    item = candidate;
                }
            }).ConfigureAwait(false);
            if (item == null)
                throw new InvalidDataException("The requested physical page list item was not returned.");

            string pageUrl = ResolvePhysicalPageUrl(item.Values, row.Url);
            var contentType = GetFieldValue(item, ContentTypeIdField, row.ContentTypeId);
            bool isPublishing = SharePointLiveAspxDiscoveryProvider.IsPublishingPageContentType(contentType);
            var page = new ClassicPage
            {
                ScanId = row.ScanId,
                SiteUrl = row.SiteUrl,
                WebUrl = row.WebUrl,
                PageUrl = pageUrl,
                PageName = GetFieldValue(item, TitleField, "") != "" ? GetFieldValue(item, TitleField, "") : Path.GetFileNameWithoutExtension(pageUrl),
                ListUrl = list.RootFolder.ServerRelativeUrl,
                ListTitle = list.Title,
                ListId = list.Id,
                ModifiedAt = GetFieldValue<DateTime>(item, ModifiedField),
                ModifiedBy = GetModifiedBy(item.Values, skipUserInformation),
                PageType = isPublishing ? PublishingPage : GetPageType(item),
                HomePage = ResolveHomePageState(pageUrl, welcomePage,
                    welcomePageKnown ?? welcomePage != null, row.HomePage),
            };

            return new PageEnrichmentInput
            {
                Page = page,
                WikiFieldHtml = isPublishing ? null : GetFieldValue<string>(item, WikiField),
                FileLeafRef = GetFieldValue(item, FileLeafRefField, ""),
            };
        }

        internal static void ApplyDiscoveryState(ClassicPage page, ClassicPageDiscovery found)
        {
            page.SiteCollectionId = found.SiteCollectionId;
            page.WebId = found.WebId;
            page.FileUniqueId = found.FileUniqueId;
            page.ListItemId = found.ListItemId;
            page.HomePage = found.HomePage ?? page.HomePage;
            page.DiscoveryStatus = found.DiscoveryStatus;
            page.AssessmentStatus = found.AssessmentStatus;
        }

        internal static bool? ResolveHomePageState(string pageUrl, string welcomePage,
            bool welcomePageKnown, bool? discoveredHomePage) => welcomePageKnown
            ? HomePageDetector.IsHomePage(pageUrl, welcomePage)
            : discoveredHomePage;

        private static void AddSitePage(PageDiscovery disc, PageEnrichmentInput input)
        {
            var pageToAdd = input.Page;
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
                    disc.EnrichmentInputs.Add(input);
                }
            }
            else
            {
                disc.ModernPageCounter++;
            }
        }

        private static void AddPublishingPage(PageDiscovery disc, PageEnrichmentInput input)
        {
            input.Page.RemediationCode = RemediationCodes.CP3.ToString();
            disc.Pages.Add(input.Page);
            // CP3 = Publishing page (matches input.Page.RemediationCode). Pre-T8 this added CP4 (Blog page),
            // a copy-paste quirk that mislabeled a publishing web's aggregated remediation codes.
            disc.RemediationCodes.Add(RemediationCodes.CP3.ToString());

            disc.EnrichmentInputs.Add(input);
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
        // HomePageOnly filter. Keep an unavailable read distinct from a successful empty value, because
        // an empty WelcomePage legitimately means default.aspx while a failed read remains unknown.
        private static async Task<(string WelcomePage, bool IsKnown)> GetWelcomePageAsync(ClientContext csomContext)
        {
            try
            {
                var rootFolder = csomContext.Web.RootFolder;
                csomContext.Load(rootFolder, f => f.WelcomePage);
                await csomContext.ExecuteQueryAsync().ConfigureAwait(false);

                return (rootFolder.WelcomePage ?? "", true);
            }
            catch
            {
                return (null, false);
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

        private static string PageQuery(List<string> extraFields, bool filterOnASPXPages = true,
            int? itemId = null, bool skipUserInformation = false)
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

            if (itemId.HasValue)
            {
                filter = $"<Query><Where><Eq><FieldRef Name='ID' /><Value Type='Counter'>{itemId.Value}</Value></Eq></Where></Query>";
            }
            else if (filterOnASPXPages)
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
                    <FieldRef Name='ID' />
                    <FieldRef Name='{ContentTypeIdField}' />
                    <FieldRef Name='{FileRefField}' />
                    <FieldRef Name='{FileLeafRefField}' />
                    <FieldRef Name='{FileTypeField}' />
                    <FieldRef Name='{ModifiedField}' />
                    {(skipUserInformation ? string.Empty : $"<FieldRef Name='{ModifiedByField}' />")}
                    <FieldRef Name='{CreatedField}' />
                    <FieldRef Name='{TitleField}' />
                    <FieldRef Name='{BSNField}' />
                    {extraViewFields}
                  </ViewFields>
                  {filter}
                  <OrderBy Override='TRUE'><FieldRef Name= 'ID' Ascending= 'FALSE' /></OrderBy>
                  <RowLimit Paged='TRUE'>{(itemId.HasValue ? 2 : 1000)}</RowLimit>
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
                if (fieldValues[fieldName] is T typed) return typed;
                if (typeof(T) == typeof(string))
                    return (T)(object)Convert.ToString(fieldValues[fieldName], System.Globalization.CultureInfo.InvariantCulture);
                return defaultValue;
            }

            return defaultValue;
        }

        private static string GetPageType(IListItem listItem)
        {
            return GetPageType(listItem.Values);
        }

        internal static string ResolvePhysicalPageUrl(IDictionary<string, object> fields, string discoveredUrl)
        {
            if (string.IsNullOrWhiteSpace(discoveredUrl) || !discoveredUrl.StartsWith('/'))
                throw new InvalidDataException("Physical page assessment requires its discovered server-relative URL; ListItemId is not a URL.");
            var fileRef = GetFieldValue(fields, FileRefField, string.Empty);
            if (!string.IsNullOrWhiteSpace(fileRef) && !string.Equals(fileRef, discoveredUrl, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The list item's FileRef differs from the discovered file; its owner/identity must be resolved before assessment.");
            return discoveredUrl;
        }

        // Pure classification over the raw field values (no CSOM) so it is unit-testable.
        internal static string GetPageType(IDictionary<string, object> fieldValues)
        {
            if (GetFieldValue(fieldValues, HtmlFileTypeField, string.Empty) == "SharePoint.WebPartPage.Document")
            {
                return WebPartPage;
            }

            if (Guid.TryParse(GetFieldValue(fieldValues, ClientSideApplicationIdField, string.Empty), out var clientApplicationId) &&
                clientApplicationId == FeatureId_Web_ModernPage)
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

            public bool WelcomePageKnown { get; init; }

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
        internal sealed class PageEnrichmentInput
        {
            public ClassicPage Page { get; init; }

            public string WikiFieldHtml { get; init; }

            public string FileLeafRef { get; init; }
        }
    }
}
