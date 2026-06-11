using Microsoft.SharePoint.Client;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal static class PageScanComponent
    {
        private static readonly Guid FeatureId_Site_Publishing = new("F6924D36-2FA8-4F0B-B16D-06B7250180FA");
        private static readonly Guid FeatureId_Web_Publishing = new("94C94CA6-B32F-4DA9-A9E3-1F3D343D7ECB");
        private static readonly Guid FeatureId_Web_ModernPage = new("B6917CB1-93A0-4B97-A84D-7CF49975D4EC");

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

        internal static async Task ExecuteAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext)
        {
            
            List<ClassicPage> pagesList = new();
            int modernPageCounter = 0;
            HashSet<string> remediationCodes = new();

            // Page ModifiedBy is a user lookup; skip it when user information is not wanted.
            bool skipUserInformation = ((ClassicScanner)scannerBase).Options.SkipUserInformation;

            var lists = ScannerBase.CleanLoadedLists(context);

            bool sitePublishingEnabled = FeatureEnabled(context.Site.Features, FeatureId_Site_Publishing);
            bool webPublishingEnabled = FeatureEnabled(context.Web.Features, FeatureId_Web_Publishing);

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
                            pagesList.Add(new ClassicPage
                            {
                                ScanId = scannerBase.ScanId,
                                SiteUrl = scannerBase.SiteUrl,
                                WebUrl = scannerBase.WebUrl,
                                PageUrl = GetFieldValue(listItem, FileRefField, $"{listItem.Id}"),
                                PageName = GetFieldValue(listItem, TitleField, ""),
                                ListUrl = blogList.RootFolder.ServerRelativeUrl,
                                ListTitle = blogList.Title,
                                ListId = blogList.Id,
                                ModifiedAt = GetFieldValue<DateTime>(listItem, ModifiedField),
                                ModifiedBy = GetModifiedBy(listItem.Values, skipUserInformation),
                                PageType = BlogPage,
                                RemediationCode = RemediationCodes.CP4.ToString(),
                            });

                            remediationCodes.Add(RemediationCodes.CP4.ToString());
                        }
                    }).ConfigureAwait(false);
                }
            }
            else if ((scannerBase.WebTemplate == "BLANKINTERNET#0" || scannerBase.WebTemplate == "ENTERWIKI#0" || 
                      scannerBase.WebTemplate == "SRCHCEN#0" || scannerBase.WebTemplate == "CMSPUBLISHING#0") &&
                     sitePublishingEnabled && webPublishingEnabled)
            {
                await QueryPublishingPagesAsync(scannerBase, pagesList, lists, remediationCodes, skipUserInformation).ConfigureAwait(false);
            }
            else
            {
                // A team site can also have the publishing features enabled
                if (sitePublishingEnabled && webPublishingEnabled)
                {
                    await QueryPublishingPagesAsync(scannerBase, pagesList, lists, remediationCodes, skipUserInformation).ConfigureAwait(false);
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
                                var pageToAdd = new ClassicPage
                                {
                                    ScanId = scannerBase.ScanId,
                                    SiteUrl = scannerBase.SiteUrl,
                                    WebUrl = scannerBase.WebUrl,
                                    PageUrl = GetFieldValue(listItem, FileRefField, $"{listItem.Id}"),
                                    PageName = GetFieldValue(listItem, TitleField, "") != "" ? GetFieldValue(listItem, TitleField, "") : Path.GetFileNameWithoutExtension(GetFieldValue(listItem, FileRefField, $"{listItem.Id}")),
                                    ListUrl = sitePagesLibrary.RootFolder.ServerRelativeUrl,
                                    ListTitle = sitePagesLibrary.Title,
                                    ListId = sitePagesLibrary.Id,
                                    ModifiedAt = GetFieldValue<DateTime>(listItem, ModifiedField),
                                    ModifiedBy = GetModifiedBy(listItem.Values, skipUserInformation),
                                    PageType = GetPageType(listItem)
                                };

                                switch (pageToAdd.PageType)
                                {
                                    case WikiPage:
                                        pageToAdd.RemediationCode = RemediationCodes.CP2.ToString();
                                        remediationCodes.Add(RemediationCodes.CP2.ToString());
                                        break;
                                    case WebPartPage:
                                        pageToAdd.RemediationCode = RemediationCodes.CP1.ToString();
                                        remediationCodes.Add(RemediationCodes.CP1.ToString());
                                        break;
                                    case ASPXPage:
                                        pageToAdd.RemediationCode = RemediationCodes.CP5.ToString();
                                        remediationCodes.Add(RemediationCodes.CP5.ToString());
                                        break;
                                }

                                if (pageToAdd.AddToDatabase())
                                {
                                    pagesList.Add(pageToAdd);
                                }
                                else
                                {
                                    modernPageCounter++;
                                }
                            }
                        }).ConfigureAwait(false);
                    }
                }
            }

            if (pagesList.Count > 0)
            {
                await scannerBase.StorageManager.StorePageInformationAsync(scannerBase.ScanId, pagesList);
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
                                                                   modernPageCounter, wikiPageCounter, blogPageCounter, webPartPageCounter, aspxPageCounter, publishingPageCounter);
        }

        private static async Task QueryPublishingPagesAsync(ScannerBase scannerBase, List<ClassicPage> pagesList, List<IList> lists, HashSet<string> remediationCodes, bool skipUserInformation)
        {
            var pagesLibrary = lists.FirstOrDefault(l => l.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.PublishingPagesLibrary);
            if (pagesLibrary != null)
            {
                // Load publishing pages, this is a publishing portal
                await QueryListAsync(pagesLibrary, PageQuery(new List<string> { }), (IEnumerable<IListItem> listItems) =>
                {
                    foreach (var listItem in listItems)
                    {
                        pagesList.Add(new ClassicPage
                        {
                            ScanId = scannerBase.ScanId,
                            SiteUrl = scannerBase.SiteUrl,
                            WebUrl = scannerBase.WebUrl,
                            PageUrl = GetFieldValue(listItem, FileRefField, $"{listItem.Id}"),
                            PageName = GetFieldValue(listItem, TitleField, "") != "" ? GetFieldValue(listItem, TitleField, "") : Path.GetFileNameWithoutExtension(GetFieldValue(listItem, FileRefField, $"{listItem.Id}")),
                            ListUrl = pagesLibrary.RootFolder.ServerRelativeUrl,
                            ListTitle = pagesLibrary.Title,
                            ListId = pagesLibrary.Id,
                            ModifiedAt = GetFieldValue<DateTime>(listItem, ModifiedField),
                            ModifiedBy = GetModifiedBy(listItem.Values, skipUserInformation),
                            PageType = PublishingPage,
                            RemediationCode = RemediationCodes.CP3.ToString(),
                        });

                        remediationCodes.Add(RemediationCodes.CP4.ToString());
                    }
                }).ConfigureAwait(false);
            }
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
    }
}
