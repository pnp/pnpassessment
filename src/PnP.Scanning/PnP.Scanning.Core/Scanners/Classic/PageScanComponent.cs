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

        internal static Task ExecuteAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext,
            IReadOnlyList<ClassicPageDiscovery> discoveredPages) =>
            ClassicPageLegacyAdapter.ExecuteAsync((ClassicScanner)scannerBase, context, csomContext, discoveredPages);

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
        }

        internal static bool? ResolveHomePageState(string pageUrl, string welcomePage,
            bool welcomePageKnown, bool? discoveredHomePage) => welcomePageKnown
            ? HomePageDetector.IsHomePage(pageUrl, welcomePage)
            : discoveredHomePage;

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

        internal static string PageQuery(List<string> extraFields, bool filterOnASPXPages = true,
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
        internal static string GetPageType(IDictionary<string, object> fieldValues) => ClassicPageRules.GetPageType(fieldValues);

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
