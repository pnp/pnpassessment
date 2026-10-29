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
        internal static readonly Guid FeatureId_Site_Publishing = new("F6924D36-2FA8-4F0B-B16D-06B7250180FA");
        internal static readonly Guid FeatureId_Web_Publishing = new("94C94CA6-B32F-4DA9-A9E3-1F3D343D7ECB");
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

        // Assessment stage recorded on a page whose list-item surface could not be opened.
        private const string ItemMetadataStage = "PageItemMetadata";

        // Four different facts, never collapsed into one. The first two are confirmed structural
        // absences — a web-root file belongs to no list, and a file can exist without a backing item —
        // while the last two are read gaps: the file names an owning list the web did not return, or it
        // lies inside a loaded list whose association this assessment never observed.
        internal const string ListAssociationAbsentCode = "list_association_absent";
        internal const string ListItemAbsentCode = "list_item_absent";
        internal const string OwningListUnresolvedCode = "owning_list_unresolved";
        internal const string ListAssociationUnknownCode = "list_association_unknown";

        // The file's vti_setuppath proves it ghosts from a stock list view/form template.
        internal const string SystemPageTemplateCode = "system_page_template";

        /// <summary>
        /// Assesses already discovered physical pages and independently restores eligible legacy item-only
        /// display-form pages. Honors HOME-only selection, retains file-only assessment and per-page failures,
        /// and persists legacy report rows and summaries without adding items to the physical census; cancellation propagates.
        /// </summary>
        internal static async Task ExecuteAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext,
            IReadOnlyList<ClassicPageDiscovery> discoveredPages)
        {
            // One resolution of everything this web supplies, so the steps below declare what they
            // need instead of reaching for a context.
            var web = await ClassicPageWebContext.CreateAsync(scannerBase, context, csomContext).ConfigureAwait(false);
            var options = web.Options;

            List<ClassicPage> pagesList = new();
            List<PageEnrichmentInput> enrichmentInputs = new();
            HashSet<string> remediationCodes = new();

            var discovery = new PageDiscovery
            {
                ScannerBase = scannerBase,
                WelcomePage = web.WelcomePage,
                WelcomePageKnown = web.WelcomePageKnown,
                HomePageOnly = options.HomePageOnly,
                SkipUserInformation = options.SkipUserInformation,
                Pages = pagesList,
                RemediationCodes = remediationCodes,
                EnrichmentInputs = enrichmentInputs,
            };

            var lists = web.Lists;
            var discoveryWriter = new AssessmentDiscoveryWriter(scannerBase.ScanId);

            // Item-only content pages remain part of the legacy assessment/report, not the physical
            // ASPX census. Never manufacture a discovery row or bind one item to the shared form file.
            await AddDisplayFormPagesAsync(discovery, context.Uri, lists, web.CancellationToken).ConfigureAwait(false);

            // The provider has already found the physical pages in the requested page scope. Do not
            // rediscover selected libraries or gate their existence on publishing features/template.
            foreach (var row in discoveredPages)
            {
                // The discovery provider records a nullable value so an unavailable modeled call is
                // not mistaken for false. Prefer the later CSOM read when it succeeded; it is also the
                // value used by the page assessment and keeps --homepageonly from filtering every page
                // merely because the discovery-specific WelcomePage read failed.
                row.HomePage = ResolveHomePageState(row.Url, web.WelcomePage, web.WelcomePageKnown, row.HomePage);
                if (options.HomePageOnly && row.HomePage != true)
                {
                    row.AssessmentStatus = "NotSelected";
                    continue;
                }
                var list = web.FindList(row.ListId);
                if (list == null || row.ListItemId == null)
                {
                    // Only the item-dependent reads are unavailable here, not the page itself: a file
                    // still resolves by URL alone, so it is assessed at file level instead of skipped.
                    AddFileLevelPage(discovery, row, ClassifyListAssociation(row, listMatched: list != null,
                        web.ListRootFolderUrls));
                    continue;
                }
                try
                {
                    var input = await LoadPhysicalPageAsync(list, row, web.WelcomePage,
                        options.SkipUserInformation, web.WelcomePageKnown).ConfigureAwait(false);
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
            }
            // Commit one Web's assessment dispositions in a single EF transaction.
            await discoveryWriter.UpdateExistingAsync(discoveredPages).ConfigureAwait(false);

            // Enrich the discovered classic pages with their web part inventory, mapping readiness, page
            // layout and (for the home page) the uncustomized-home-page verdict. Captured for enrichment
            // are the item-backed web part / wiki / publishing pages plus every page assessed from its
            // file alone. Per-page failures are logged and skipped — a single bad page must not fail the
            // web — and are recorded as a failure rather than as an empty web part inventory.
            List<ClassicPageWebPart> webPartsList = new();
            foreach (var input in enrichmentInputs)
            {
                try
                {
                    var pageWebParts = await ExtractWebPartsAsync(csomContext, input, options.ExportWebPartProperties).ConfigureAwait(false);

                    // Stamps IsMappable per web part + WebPartCount / MappingPercentage / UnmappedWebParts on the page.
                    if (ShouldRollUpMapping(input, pageWebParts.Count))
                    {
                        PageMappingCalculator.ApplyMapping(input.Page, pageWebParts, MappingManager);
                    }

                    webPartsList.AddRange(pageWebParts);

                    if (input.Page.HomePage == true)
                    {
                        input.Page.UncustomizedHomePage = await DetermineUncustomizedHomePageAsync(
                            scannerBase, context, csomContext, input, pageWebParts,
                            web.SitePublishingEnabled, web.WebPublishingEnabled).ConfigureAwait(false);
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
                    if (found == null) continue;
                    ApplyDiscoveryState(page, found);
                }
                await scannerBase.StorageManager.StorePageInformationAsync(scannerBase.ScanId, pagesList);
            }

            if (webPartsList.Count > 0)
            {
                await scannerBase.StorageManager.StorePageWebPartsAsync(scannerBase.ScanId, webPartsList);
            }

            var counts = CountAssessedPages(pagesList);
            await scannerBase.StorageManager.StorePageSummaryAsync(scannerBase.ScanId, scannerBase.SiteUrl,
                scannerBase.WebUrl, scannerBase.WebTemplate, context, remediationCodes, discovery.ModernPageCounter,
                counts.Wiki, counts.Blog, counts.WebPart, counts.Aspx, counts.Publishing).ConfigureAwait(false);
        }

        /// <summary>
        /// Adds independently assessed item-only pages and their remediation codes to the legacy report
        /// branch. Does not write discovery rows, create file bindings or queue per-item web-part reads.
        /// </summary>
        internal static async Task AddDisplayFormPagesAsync(PageDiscovery discovery, Uri contextUri,
            IReadOnlyList<IList> lists, CancellationToken cancellationToken)
        {
            var scanner = discovery.ScannerBase;
            var pages = await LegacyDisplayFormAssessment.ReadAsync(scanner.ScanId, scanner.SiteUrl,
                scanner.WebUrl, contextUri, lists, discovery.HomePageOnly, discovery.SkipUserInformation,
                cancellationToken).ConfigureAwait(false);
            discovery.Pages.AddRange(pages);
            discovery.RemediationCodes.UnionWith(pages.Select(page => page.RemediationCode));
        }

        /// <summary>
        /// Counts the final legacy assessment rows, including item-only blog/custom-form pages, without
        /// changing the independently acquired physical inventory or counting modern pages as classic.
        /// </summary>
        internal static (int Wiki, int Blog, int WebPart, int Aspx, int Publishing) CountAssessedPages(
            IReadOnlyList<ClassicPage> pages)
        {
            int wiki = 0;
            int blog = 0;
            int webPart = 0;
            int aspx = 0;
            int publishing = 0;
            foreach (var page in pages)
            {
                switch (page.PageType)
                {
                    case WikiPage:
                        wiki++;
                        break;
                    case BlogPage:
                        blog++;
                        break;
                    case WebPartPage:
                        webPart++;
                        break;
                    case ASPXPage:
                        aspx++;
                        break;
                    case PublishingPage:
                        publishing++;
                        break;
                }
            }

            return (wiki, blog, webPart, aspx, publishing);
        }

        // Kept independent of ScannerBase so the same SDK field selection and metadata projection
        // can be replayed offline against the assessment database/report pipeline.
        internal static async Task<PageEnrichmentInput> LoadPhysicalPageAsync(IList list, ClassicPageDiscovery row,
            string welcomePage, bool skipUserInformation, bool? welcomePageKnown = null)
        {
            if (row.ListId != list.Id || row.ListItemId == null || row.ListItemId <= 0)
                throw new InvalidDataException("Physical page metadata requires its discovered list and list-item identity.");

            // REST $select=* (IListItem.All) still omits computed fields such as FileRef,
            // HTML_x0020_File_x0020_Type and ClientSideApplicationId. Reuse the assessment
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
            page.CustomizedPageStatus = found.CustomizedPageStatus;
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

        // Assesses a discovered file whose list-item surface is unavailable, whether because the file is
        // not list-backed at all, because it has no backing item, or because its owning list was not
        // matched. What is lost is the item-dependent reads (item field values, ModifiedAt/ModifiedBy,
        // WikiField, publishing fields), not the page: PageWebPartExtractor.ExtractFromWebPartPageAsync
        // resolves the page through Web.GetFileByServerRelativeUrl and reads its LimitedWebPartManager,
        // with no list item involved. Verified on a live tenant: a web's root default.aspx, newsfeed.aspx
        // and GettingStarted.aspx belong to no list and have no list item, yet both their vti_setuppath
        // and their web parts read from the file alone.
        internal static void AddFileLevelPage(PageDiscovery disc, ClassicPageDiscovery row,
            ListAssociationState association)
        {
            // Recorded as separate limitations so the conditions stay distinguishable downstream.
            if (association == ListAssociationState.OwningListUnresolved)
            {
                AssessmentWebDiscovery.AddError(row, ItemMetadataStage, OwningListUnresolvedCode,
                    "The file names an owning list that was not matched in this web, so its list-item association could not be read.");
            }
            else if (association == ListAssociationState.Unknown)
            {
                AssessmentWebDiscovery.AddError(row, ItemMetadataStage, ListAssociationUnknownCode,
                    "The file lies inside a loaded list, so it is list-backed, but no observation of this file reported its " +
                    "list association: the identity is unknown rather than absent.");
            }
            else if (row.ListId == null)
            {
                AssessmentWebDiscovery.AddError(row, ItemMetadataStage, ListAssociationAbsentCode,
                    "The file is not list-backed: no owning list is recorded, so no list-item surface exists to read.");
            }
            if (row.ListItemId == null)
            {
                AssessmentWebDiscovery.AddError(row, ItemMetadataStage, ListItemAbsentCode,
                    "The file has no backing list item: item field values, ModifiedAt/ModifiedBy and WikiField are not applicable.");
            }

            // The analyzer is the only reader of the setup path, so the provisioning fact is
            // established once and the page type below is a projection of it rather than a second,
            // independently drifting reading.
            var provisioning = ProvisioningTemplateAnalyzer.Observe(row);
            var family = ProvisioningTemplateAnalyzer.FamilyOf(provisioning);
            string pageType = FileLevelPageType(family);
            if (pageType == null)
            {
                // A server-recorded provisioning fact, not a guess from the URL or from an empty item:
                // the file ghosts from the single view/form template every list provisions, so it is a
                // system surface carrying no page content of its own.
                AssessmentWebDiscovery.AddError(row, ItemMetadataStage, SystemPageTemplateCode,
                    $"The file ghosts from the stock {family} template '{row.SetupPath}'.");
                row.AssessmentStatus = "NotApplicable";
                return;
            }
            if (string.IsNullOrWhiteSpace(row.Url) || !row.Url.StartsWith('/'))
            {
                // Everything below opens the page by URL alone, so without one there is no surface to read.
                AssessmentWebDiscovery.AddError(row, ItemMetadataStage, DiscoveryGapCodes.IdentityMissing,
                    "The discovered file has no server-relative URL, so no file-level page surface could be opened.");
                row.AssessmentStatus = "Failed";
                return;
            }

            var page = new ClassicPage
            {
                ScanId = row.ScanId,
                SiteUrl = row.SiteUrl,
                WebUrl = row.WebUrl,
                PageUrl = row.Url,
                PageName = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(row.FileName) ? row.Url : row.FileName),
                SiteCollectionId = row.SiteCollectionId,
                WebId = row.WebId,
                FileUniqueId = row.FileUniqueId,
                // ListId/ListItemId, ListUrl/ListTitle and the Modified* values come from the list item
                // that was never read; they stay unset rather than being filled with a stand-in.
                PageType = pageType,
                HomePage = row.HomePage,
                RemediationCode = (pageType == WebPartPage ? RemediationCodes.CP1 : RemediationCodes.CP5).ToString(),
            };
            row.PageType = pageType;
            disc.Pages.Add(page);
            disc.RemediationCodes.Add(page.RemediationCode);
            disc.EnrichmentInputs.Add(new PageEnrichmentInput
            {
                Page = page,
                FileLeafRef = row.FileName,
                FileLevelOnly = true,
            });

            // Only an unresolved association leaves the page assessment incomplete — its item metadata may
            // well exist and simply was not reachable. A confirmed-absent list association or item does
            // not: everything that can be assessed about such a file is assessed here, with the
            // inapplicable item-dependent operations recorded above.
            row.AssessmentStatus = association == ListAssociationState.Resolved ? "Complete" : "Failed";
        }

        // How much is actually known about a file's owning list, kept distinguishable because the three
        // conditions carry different evidence and must not share one verdict.
        internal enum ListAssociationState
        {
            // A confirmed structural fact: the file belongs to no list, so there is no item surface.
            Resolved,
            // The file names a list the web's loaded (unfiltered) set did not return.
            OwningListUnresolved,
            // The file lies inside a loaded list, so an association must exist, but no observation of
            // this file reported one.
            Unknown,
        }

        // Separates the ways a file can reach the assessment without a usable owning list.
        //
        // A null ListId is not self-evidently an absent association. The modeled web-root traversal
        // recursively visits every folder under the web root, including library folders, and
        // ISharePointAspxRestClient.ReadFolderAsync does not request list or item identity, so its
        // records always carry null IDs. While the richer per-list observation also succeeds the two
        // merge and the identity is restored, but when that observation is denied or fails the modeled
        // null is all that survives. Treating it as confirmed absence would report a list-backed page as
        // fully assessed while its item content was never read.
        //
        // The file's own URL settles it against the same loaded list set already used above: a file
        // inside a list's root folder is list-backed whatever the discovery record says, so a null there
        // is unknown rather than absent. A file outside every list root folder - the normal state of the
        // web-root entry pages this route exists to assess - keeps its confirmed-absence verdict.
        internal static ListAssociationState ClassifyListAssociation(ClassicPageDiscovery row,
            bool listMatched, IEnumerable<string> listRootFolderUrls)
        {
            if (row.ListId != null)
                return listMatched ? ListAssociationState.Resolved : ListAssociationState.OwningListUnresolved;
            return IsInsideAnyListRootFolder(row.Url, listRootFolderUrls)
                ? ListAssociationState.Unknown : ListAssociationState.Resolved;
        }

        // Containment is matched on a folder boundary so that a sibling such as "/PagesArchive" is never
        // read as living inside "/Pages".
        internal static bool IsInsideAnyListRootFolder(string url, IEnumerable<string> listRootFolderUrls)
        {
            if (string.IsNullOrWhiteSpace(url) || listRootFolderUrls == null) return false;
            var path = url.TrimEnd('/');
            foreach (var root in listRootFolderUrls)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var prefix = root.TrimEnd('/');
                if (prefix.Length == 0) continue;
                if (path.Length > prefix.Length && path[prefix.Length] == '/' &&
                    path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // Classifies a file-level page only as far as choosing an applicable extractor. The stock list
        // view/form templates have no page content surface to read, a proven web part page template is
        // reported as one (its zones and layout then read exactly), and anything else — an unghosted
        // user-authored file or an unrecognised template such as SiteTemplates\STS\default.aspx — stays
        // the residual ASPXPage and is still read, rather than being guessed into a system-page or wiki
        // conclusion. Deciding what such a page is actually for is deliberately not done here.
        internal static string FileLevelPageType(SetupPathPageFamily family) => family switch
        {
            SetupPathPageFamily.ListView or SetupPathPageFamily.ListForm => null,
            SetupPathPageFamily.WebPartPage => WebPartPage,
            _ => ASPXPage,
        };

        // The legacy "no web parts means 100% mappable" convention assumes a page's whole content surface
        // was read. A file-level assessment reads only the file's web part zones, so an empty result there
        // is not evidence that the page converts without loss; its readiness rollup is left uncomputed and
        // the recorded item-metadata limitation carries the reason instead.
        internal static bool ShouldRollUpMapping(PageEnrichmentInput input, int extractedWebParts) =>
            extractedWebParts > 0 || !input.FileLevelOnly;

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
        internal static async Task<List<ClassicPageWebPart>> ExtractWebPartsAsync(ClientContext csomContext, PageEnrichmentInput input, bool exportWebPartProperties)
        {
            // A file-level page has no list item, so there is no WikiField or publishing field to resolve
            // placeholders against. The web part manager read is the one extractor that needs nothing but
            // the file URL, so it is used whatever the page type: never the "no web parts" arm below,
            // which would report an unread page as empty.
            if (input.FileLevelOnly)
            {
                return await PageWebPartExtractor.ExtractFromWebPartPageAsync(csomContext, input.Page, exportWebPartProperties).ConfigureAwait(false);
            }

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

        /// <summary>Builds the recursive page-assessment CAML with requested extra fields; an explicit item ID takes precedence over the optional ASPX filter, and user metadata is omitted only when requested.</summary>
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
        internal sealed class PageDiscovery
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

            // Set when the page was assessed from its file alone because no list item was readable.
            public bool FileLevelOnly { get; init; }
        }
    }
}
