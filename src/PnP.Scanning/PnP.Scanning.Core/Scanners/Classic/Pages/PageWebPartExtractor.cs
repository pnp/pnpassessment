using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.WebParts;
using PnP.Scanning.Core.Scanners.WebPartMapping;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Turns a discovered classic page into its per-web-part inventory (<see cref="ClassicPageWebPart"/>
    /// rows) using CSOM. Two extraction paths, mirroring the legacy Modernization Scanner:
    /// <list type="bullet">
    /// <item><description><b>Web part pages</b> — read the web parts straight off the page's
    /// <c>LimitedWebPartManager</c>.</description></item>
    /// <item><description><b>Wiki pages</b> — parse the <c>WikiField</c> HTML (pure, via
    /// <see cref="WikiContentParser"/>) into text blocks + web part placeholders, then resolve the
    /// placeholders against the page's <c>LimitedWebPartManager</c>.</description></item>
    /// </list>
    /// The <c>LimitedWebPartManager</c> round-trips cannot be unit-tested in isolation, so the live path
    /// is exercised by the integration test; only the wiki-HTML parsing has its own unit tests. This
    /// component intentionally does not persist anything (that is done by the storage layer) and does not
    /// compute the mapping percentage (that is a separate task) — it just produces the rows.
    /// </summary>
    internal static class PageWebPartExtractor
    {
        // Web part type constants returned by the property-signature based type detection. Ported from
        // the legacy WebParts constants (only the subset GetTypeFromProperties can return is needed).
        private const string XsltListViewType = "Microsoft.SharePoint.WebPartPages.XsltListViewWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string ListViewType = "Microsoft.SharePoint.WebPartPages.ListViewWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string MediaType = "Microsoft.SharePoint.Publishing.WebControls.MediaWebPart, Microsoft.SharePoint.Publishing, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string PictureLibrarySlideshowType = "Microsoft.SharePoint.WebPartPages.PictureLibrarySlideshowWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string ChartType = "Microsoft.Office.Server.WebControls.ChartWebPart, Microsoft.Office.Server.Chart, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string MembersType = "Microsoft.SharePoint.WebPartPages.MembersWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string SilverlightType = "Microsoft.SharePoint.WebPartPages.SilverlightWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string ClientType = "Microsoft.SharePoint.WebPartPages.ClientWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string ContentEditorType = "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string ImageType = "Microsoft.SharePoint.WebPartPages.ImageWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string TitleBarType = "Microsoft.SharePoint.WebPartPages.TitleBarWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string ScriptEditorType = "Microsoft.SharePoint.WebPartPages.ScriptEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string SPUserCodeType = "Microsoft.SharePoint.WebPartPages.SPUserCodeWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

        private const string UnsupportedWebPartType = "Unsupported Web Part Type";

        // The publishing page list item field that holds the page layout reference (a URL field whose
        // Description is the layout's friendly name). Ported from the legacy Constants.PublishingPageLayoutField.
        private const string PublishingPageLayoutField = "PublishingPageLayout";

        /// <summary>
        /// Extracts the web part inventory for a web part page using its <c>LimitedWebPartManager</c>.
        /// </summary>
        /// <param name="csomContext">CSOM context for the web (already throttle-aware).</param>
        /// <param name="page">The discovered classic page the rows belong to.</param>
        /// <param name="exportWebPartProperties">When set, the web part properties are serialized to JSON.</param>
        /// <param name="includeTitleBarWebPart">Include the TitleBar zone web part (off by default, as legacy).</param>
        internal static async Task<List<ClassicPageWebPart>> ExtractFromWebPartPageAsync(ClientContext csomContext, ClassicPage page,
                                                                                         bool exportWebPartProperties, bool includeTitleBarWebPart = false)
        {
            var webPartPage = csomContext.Web.GetFileByServerRelativeUrl(page.PageUrl);

            var pageProperties = webPartPage.Properties;
            csomContext.Load(pageProperties);

            var limitedWPManager = webPartPage.GetLimitedWebPartManager(PersonalizationScope.Shared);
            csomContext.Load(limitedWPManager);

            var webParts = csomContext.LoadQuery(limitedWPManager.WebParts.IncludeWithDefaultProperties(
                wp => wp.Id, wp => wp.ZoneId, wp => wp.WebPart.ExportMode, wp => wp.WebPart.Title,
                wp => wp.WebPart.ZoneIndex, wp => wp.WebPart.IsClosed, wp => wp.WebPart.Hidden, wp => wp.WebPart.Properties));
            await csomContext.ExecuteQueryAsync().ConfigureAwait(false);

            var layout = GetWebPartPageLayout(pageProperties);
            page.Layout = PageLayoutDetector.ToLayoutString(layout);

            // Export the web part XML for the parts that allow it (gives the most reliable type).
            var exportedXml = new Dictionary<Guid, ClientResult<string>>();
            bool isDirty = false;
            foreach (var foundWebPart in webParts)
            {
                if (!includeTitleBarWebPart && foundWebPart.ZoneId.Equals("TitleBar", StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                if (foundWebPart.WebPart.ExportMode == WebPartExportMode.All)
                {
                    exportedXml[foundWebPart.Id] = limitedWPManager.ExportWebPart(foundWebPart.Id);
                    isDirty = true;
                }
            }
            if (isDirty)
            {
                await csomContext.ExecuteQueryAsync().ConfigureAwait(false);
            }

            var entities = new List<WebPartEntity>();
            foreach (var foundWebPart in webParts)
            {
                if (!includeTitleBarWebPart && foundWebPart.ZoneId.Equals("TitleBar", StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                exportedXml.TryGetValue(foundWebPart.Id, out var xml);
                string webPartType = xml != null ? GetTypeFromXml(xml.Value) : GetTypeFromProperties(foundWebPart.WebPart.Properties.FieldValues);

                entities.Add(new WebPartEntity
                {
                    Title = foundWebPart.WebPart.Title,
                    Type = webPartType,
                    Id = foundWebPart.Id,
                    ServerControlId = foundWebPart.Id.ToString(),
                    Row = GetRow(foundWebPart.ZoneId, layout),
                    Column = GetColumn(foundWebPart.ZoneId, layout),
                    Order = foundWebPart.WebPart.ZoneIndex,
                    ZoneId = foundWebPart.ZoneId,
                    ZoneIndex = (uint)foundWebPart.WebPart.ZoneIndex,
                    IsClosed = foundWebPart.WebPart.IsClosed,
                    Hidden = foundWebPart.WebPart.Hidden,
                    Properties = ToStringProperties(foundWebPart.WebPart.Properties.FieldValues),
                });
            }

            return ToRows(page, entities, exportWebPartProperties);
        }

        /// <summary>
        /// Extracts the web part inventory for a wiki page: parses the supplied <c>WikiField</c> HTML and
        /// resolves the embedded web part placeholders against the page's <c>LimitedWebPartManager</c>.
        /// </summary>
        /// <param name="csomContext">CSOM context for the web (already throttle-aware).</param>
        /// <param name="page">The discovered classic page the rows belong to.</param>
        /// <param name="wikiFieldHtml">The page's <c>WikiField</c> HTML.</param>
        /// <param name="exportWebPartProperties">When set, the web part properties are serialized to JSON.</param>
        internal static async Task<List<ClassicPageWebPart>> ExtractFromWikiPageAsync(ClientContext csomContext, ClassicPage page,
                                                                                      string wikiFieldHtml, bool exportWebPartProperties)
        {
            // Pure HTML parsing: text blocks + embedded media parts + placeholders for the embedded web parts.
            var parsed = WikiContentParser.Parse(wikiFieldHtml);

            // The wiki layout is derived from the same HTML (no CSOM needed).
            page.Layout = PageLayoutDetector.ToLayoutString(PageLayoutDetector.DetectWikiLayout(wikiFieldHtml));

            var entities = new List<WebPartEntity>(parsed.TextParts);
            entities.AddRange(parsed.MediaParts);

            if (parsed.Placeholders.Count > 0)
            {
                var wikiPage = csomContext.Web.GetFileByServerRelativeUrl(page.PageUrl);
                var limitedWPManager = wikiPage.GetLimitedWebPartManager(PersonalizationScope.Shared);
                csomContext.Load(limitedWPManager);

                var definitions = new Dictionary<WikiWebPartPlaceholder, WebPartDefinition>();
                foreach (var placeholder in parsed.Placeholders)
                {
                    // Sometimes the wiki html references web parts that are no longer on the page; the
                    // ExceptionHandlingScope lets the server tolerate those in a single round-trip.
                    var scope = new ExceptionHandlingScope(csomContext);
                    using (scope.StartScope())
                    {
                        using (scope.StartTry())
                        {
                            var definition = limitedWPManager.WebParts.GetByControlId(placeholder.ControlId);
                            csomContext.Load(definition, wp => wp.Id, wp => wp.WebPart.ExportMode, wp => wp.WebPart.Title,
                                             wp => wp.WebPart.ZoneIndex, wp => wp.WebPart.IsClosed, wp => wp.WebPart.Hidden, wp => wp.WebPart.Properties);
                            definitions[placeholder] = definition;
                        }
                        using (scope.StartCatch())
                        {
                        }
                    }
                }
                await csomContext.ExecuteQueryAsync().ConfigureAwait(false);

                // Export the web part XML for the parts that allow it.
                var exportedXml = new Dictionary<WikiWebPartPlaceholder, ClientResult<string>>();
                bool isDirty = false;
                foreach (var (placeholder, definition) in definitions)
                {
                    if (definition.ServerObjectIsNull == false && definition.WebPart.ExportMode == WebPartExportMode.All)
                    {
                        exportedXml[placeholder] = limitedWPManager.ExportWebPart(definition.Id);
                        isDirty = true;
                    }
                }
                if (isDirty)
                {
                    await csomContext.ExecuteQueryAsync().ConfigureAwait(false);
                }

                foreach (var placeholder in parsed.Placeholders)
                {
                    if (!definitions.TryGetValue(placeholder, out var definition) || definition.ServerObjectIsNull != false)
                    {
                        continue;
                    }

                    exportedXml.TryGetValue(placeholder, out var xml);
                    string webPartType = xml != null ? GetTypeFromXml(xml.Value) : GetTypeFromProperties(definition.WebPart.Properties.FieldValues);

                    entities.Add(new WebPartEntity
                    {
                        Title = definition.WebPart.Title,
                        Type = webPartType,
                        Id = definition.Id,
                        ServerControlId = placeholder.Id,
                        Row = placeholder.Row,
                        Column = placeholder.Column,
                        Order = placeholder.Order,
                        ZoneId = "",
                        ZoneIndex = (uint)definition.WebPart.ZoneIndex,
                        IsClosed = definition.WebPart.IsClosed,
                        Hidden = definition.WebPart.Hidden,
                        Properties = ToStringProperties(definition.WebPart.Properties.FieldValues),
                    });
                }
            }

            // Present the inventory in document order (row, then column, then order).
            var ordered = entities.OrderBy(w => w.Row).ThenBy(w => w.Column).ThenBy(w => w.Order).ToList();
            return ToRows(page, ordered, exportWebPartProperties);
        }

        /// <summary>
        /// Extracts the web part inventory for a publishing page using its <c>LimitedWebPartManager</c>.
        /// Ported from the legacy <c>PublishingPage.GetWebPartsForScanner</c> (the scanner-oriented variant,
        /// not the transform-oriented <c>Analyze</c>): it inventories every web part placed in the page's
        /// web part zones. Unlike <see cref="ExtractFromWebPartPageAsync"/> the web parts are not assigned a
        /// row / column (the legacy scanner does not position publishing-page parts) and there is no TitleBar
        /// exclusion — every web part the manager returns is recorded. The page's <c>Layout</c> is set to the
        /// actual page layout name (e.g. <c>ArticleLeft</c>) read from the <c>PublishingPageLayout</c> field,
        /// matching the legacy <c>PublishingAnalyzer</c> (see <see cref="GetPublishingPageLayoutName"/>).
        /// </summary>
        /// <remarks>
        /// SPO-only, matching T5: the on-premises web-services fallback
        /// (<c>LoadPublishingPageFromWebServices</c> / <c>ExtractWebPartDocumentViaWebServicesFromPage</c>) is
        /// intentionally not ported. Web parts placed outside a web part zone (e.g. directly in a field control
        /// via SharePoint Designer) are not surfaced by the web part manager and are therefore not captured,
        /// matching the legacy scanner behaviour.
        /// </remarks>
        /// <param name="csomContext">CSOM context for the web (already throttle-aware).</param>
        /// <param name="page">The discovered classic page the rows belong to.</param>
        /// <param name="exportWebPartProperties">When set, the web part properties are serialized to JSON.</param>
        internal static async Task<List<ClassicPageWebPart>> ExtractFromPublishingPageAsync(ClientContext csomContext, ClassicPage page,
                                                                                            bool exportWebPartProperties)
        {
            var publishingPage = csomContext.Web.GetFileByServerRelativeUrl(page.PageUrl);

            // The publishing page's list item carries the page layout reference we report as the layout.
            var listItem = publishingPage.ListItemAllFields;
            csomContext.Load(listItem);

            var limitedWPManager = publishingPage.GetLimitedWebPartManager(PersonalizationScope.Shared);
            csomContext.Load(limitedWPManager);

            var webParts = csomContext.LoadQuery(limitedWPManager.WebParts.IncludeWithDefaultProperties(
                wp => wp.Id, wp => wp.ZoneId, wp => wp.WebPart.ExportMode, wp => wp.WebPart.Title,
                wp => wp.WebPart.ZoneIndex, wp => wp.WebPart.IsClosed, wp => wp.WebPart.Hidden, wp => wp.WebPart.Properties));
            await csomContext.ExecuteQueryAsync().ConfigureAwait(false);

            // Parity with the legacy PublishingAnalyzer: the recorded layout is the actual page layout name
            // (e.g. "ArticleLeft") read from the PublishingPageLayout field — not the transform engine's
            // PublishingPage_AutoDetect placeholder (which the legacy scanner discards for publishing pages).
            page.Layout = GetPublishingPageLayoutName(listItem.FieldValues);

            // Export the web part XML for the parts that allow it (gives the most reliable type).
            var exportedXml = new Dictionary<Guid, ClientResult<string>>();
            bool isDirty = false;
            foreach (var foundWebPart in webParts)
            {
                if (foundWebPart.WebPart.ExportMode == WebPartExportMode.All)
                {
                    exportedXml[foundWebPart.Id] = limitedWPManager.ExportWebPart(foundWebPart.Id);
                    isDirty = true;
                }
            }
            if (isDirty)
            {
                await csomContext.ExecuteQueryAsync().ConfigureAwait(false);
            }

            var entities = new List<WebPartEntity>();
            // Process in zone-index order, as the legacy scanner does, so the resulting row order is stable.
            foreach (var foundWebPart in webParts.OrderBy(wp => wp.WebPart.ZoneIndex))
            {
                exportedXml.TryGetValue(foundWebPart.Id, out var xml);
                string webPartType = xml != null ? GetTypeFromXml(xml.Value) : GetTypeFromProperties(foundWebPart.WebPart.Properties.FieldValues);

                entities.Add(new WebPartEntity
                {
                    Title = foundWebPart.WebPart.Title,
                    Type = webPartType,
                    Id = foundWebPart.Id,
                    ServerControlId = foundWebPart.Id.ToString(),
                    // The legacy scanner does not map publishing zones to a row/column grid (that is layout
                    // detection, deferred to T7); only the zone index is meaningful here.
                    Order = foundWebPart.WebPart.ZoneIndex,
                    ZoneId = foundWebPart.ZoneId,
                    ZoneIndex = (uint)foundWebPart.WebPart.ZoneIndex,
                    IsClosed = foundWebPart.WebPart.IsClosed,
                    Hidden = foundWebPart.WebPart.Hidden,
                    Properties = ToStringProperties(foundWebPart.WebPart.Properties.FieldValues),
                });
            }

            return ToRows(page, entities, exportWebPartProperties);
        }

        private static List<ClassicPageWebPart> ToRows(ClassicPage page, List<WebPartEntity> entities, bool exportWebPartProperties)
        {
            var rows = new List<ClassicPageWebPart>();
            int index = 0;
            foreach (var entity in entities)
            {
                rows.Add(new ClassicPageWebPart
                {
                    ScanId = page.ScanId,
                    SiteUrl = page.SiteUrl,
                    WebUrl = page.WebUrl,
                    PageUrl = page.PageUrl,
                    WebPartIndex = index++,
                    WebPartType = entity.Type,
                    WebPartTypeShort = entity.TypeShort(),
                    WebPartTitle = entity.Title,
                    WebPartProperties = exportWebPartProperties ? SerializeProperties(entity.Properties) : null,
                    ZoneId = entity.ZoneId,
                    Row = entity.Row,
                    Column = entity.Column,
                    Order = entity.Order,
                    Hidden = entity.Hidden,
                    IsClosed = entity.IsClosed,
                    // IsMappable is computed later (mapping percentage task).
                });
            }

            return rows;
        }

        private static string SerializeProperties(Dictionary<string, string> properties)
        {
            if (properties == null || properties.Count == 0)
            {
                return null;
            }

            return JsonSerializer.Serialize(properties);
        }

        private static Dictionary<string, string> ToStringProperties(IDictionary<string, object> fieldValues)
        {
            var properties = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            if (fieldValues != null)
            {
                foreach (var prop in fieldValues)
                {
                    if (!properties.ContainsKey(prop.Key))
                    {
                        properties.Add(prop.Key, prop.Value?.ToString() ?? "");
                    }
                }
            }

            return properties;
        }

        // Determines the web part type from its exported XML. Ported from BasePage.GetType.
        // Internal (not private) so the publishing/web-part type detection has its own unit tests.
        internal static string GetTypeFromXml(string webPartXml)
        {
            string type = "Unknown";

            if (!string.IsNullOrEmpty(webPartXml))
            {
                var xml = XElement.Parse(webPartXml);
                var xmlns = xml.XPathSelectElement("*").GetDefaultNamespace();
                if (xmlns.NamespaceName.Equals("http://schemas.microsoft.com/WebPart/v3", StringComparison.InvariantCultureIgnoreCase))
                {
                    type = xml.Descendants(xmlns + "type").FirstOrDefault()?.Attribute("name")?.Value ?? type;
                }
                else if (xmlns.NamespaceName.Equals("http://schemas.microsoft.com/WebPart/v2", StringComparison.InvariantCultureIgnoreCase))
                {
                    type = $"{xml.Descendants(xmlns + "TypeName").FirstOrDefault()?.Value}, {xml.Descendants(xmlns + "Assembly").FirstOrDefault()?.Value}";
                }
            }

            return type;
        }

        // Determines the web part type by detecting it from the available properties. Ported from
        // BasePage.GetTypeFromProperties (online code path; the on-premises legacy branch is not ported).
        // Internal (not private) so the publishing/web-part type detection has its own unit tests.
        internal static string GetTypeFromProperties(IDictionary<string, object> properties)
        {
            if (HasAll(properties, "ListUrl", "ListId", "Xsl", "JSLink", "ShowTimelineIfAvailable")) return XsltListViewType;
            if (HasAll(properties, "ListViewXml", "ListName", "ListId", "ViewContentTypeId", "PageType")) return ListViewType;
            if (HasAll(properties, "AutoPlay", "MediaSource", "Loop", "IsPreviewImageSourceOverridenForVideoSet", "PreviewImageSource")) return MediaType;
            if (HasAll(properties, "LibraryGuid", "Layout", "Speed", "ShowToolbar", "ViewGuid")) return PictureLibrarySlideshowType;
            if (HasAll(properties, "ConnectionPointEnabled", "ChartXml", "DataBindingsString", "DesignerChartTheme")) return ChartType;
            if (HasAll(properties, "NumberLimit", "DisplayType", "MembershipGroupId", "Toolbar")) return MembersType;
            if (HasAll(properties, "MinRuntimeVersion", "WindowlessMode", "CustomInitParameters", "Url", "ApplicationXml")) return SilverlightType;
            if (HasAll(properties, "FeatureId", "ProductWebId", "ProductId")) return ClientType;
            if (HasAll(properties, "Content")) return ScriptEditorType;
            if (HasAll(properties, "CatalogIconImageUrl", "AllowEdit", "TitleIconImageUrl", "ExportMode")) return SPUserCodeType;

            return UnsupportedWebPartType;
        }

        // Reads the publishing page layout name from the page list item's PublishingPageLayout field.
        // Ported verbatim from the legacy ListItemExtensions.PageLayout: the field is a URL field whose
        // Description is the friendly layout name (e.g. "ArticleLeft"); an absent/empty field yields "".
        // Internal (not private) so the field-value parsing has its own unit tests; the CSOM list item load
        // is integration-only.
        internal static string GetPublishingPageLayoutName(IDictionary<string, object> fieldValues)
        {
            if (fieldValues != null &&
                fieldValues.TryGetValue(PublishingPageLayoutField, out var value) &&
                value != null &&
                !string.IsNullOrEmpty(value.ToString()))
            {
                var description = (value as FieldUrlValue)?.Description;
                return string.IsNullOrEmpty(description) ? "" : description;
            }

            return "";
        }

        private static bool HasAll(IDictionary<string, object> properties, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!properties.ContainsKey(key))
                {
                    return false;
                }
            }

            return true;
        }

        // Determines the web part page layout from the page file properties. Reads the vti_setuppath
        // property (CSOM) and delegates the actual string→layout mapping to the pure PageLayoutDetector
        // (ported from WebPartPage.GetLayout) so that logic is unit-testable on its own.
        private static PageLayout GetWebPartPageLayout(PropertyValues pageProperties)
        {
            var setupPath = pageProperties.FieldValues.ContainsKey("vti_setuppath")
                ? pageProperties["vti_setuppath"]?.ToString()
                : null;

            return PageLayoutDetector.DetectWebPartPageLayout(setupPath);
        }

        // Translates the given zone value and page layout to a column number. Ported from WebPartPage.GetColumn.
        private static int GetColumn(string zoneId, PageLayout layout)
        {
            switch (layout)
            {
                case PageLayout.WebPart_HeaderFooterThreeColumns:
                    if (IsZone(zoneId, "Header", "LeftColumn", "Footer")) return 1;
                    if (IsZone(zoneId, "MiddleColumn")) return 2;
                    if (IsZone(zoneId, "RightColumn")) return 3;
                    break;
                case PageLayout.WebPart_FullPageVertical:
                    return 1;
                case PageLayout.WebPart_HeaderLeftColumnBody:
                    if (IsZone(zoneId, "Header", "LeftColumn")) return 1;
                    if (IsZone(zoneId, "Body")) return 2;
                    break;
                case PageLayout.WebPart_HeaderRightColumnBody:
                    if (IsZone(zoneId, "Header", "Body")) return 1;
                    if (IsZone(zoneId, "RightColumn")) return 2;
                    break;
                case PageLayout.WebPart_HeaderFooter2Columns4Rows:
                    if (IsZone(zoneId, "Header", "Footer", "LeftColumn")) return 1;
                    if (IsZone(zoneId, "Row1", "Row2", "Row3", "Row4")) return 2;
                    if (IsZone(zoneId, "RightColumn")) return 3;
                    break;
                case PageLayout.WebPart_HeaderFooter4ColumnsTopRow:
                    if (IsZone(zoneId, "Header", "Footer", "LeftColumn")) return 1;
                    if (IsZone(zoneId, "TopRow", "CenterRightColumn", "CenterLeftColumn")) return 2;
                    if (IsZone(zoneId, "RightColumn")) return 3;
                    break;
                case PageLayout.WebPart_LeftColumnHeaderFooterTopRow3Columns:
                    if (IsZone(zoneId, "Header", "LeftColumn", "CenterLeftColumn", "Footer", "TopRow")) return 1;
                    if (IsZone(zoneId, "CenterColumn")) return 2;
                    if (IsZone(zoneId, "CenterRightColumn")) return 3;
                    break;
                case PageLayout.WebPart_RightColumnHeaderFooterTopRow3Columns:
                    if (IsZone(zoneId, "Header", "RightColumn", "CenterLeftColumn", "Footer", "TopRow")) return 1;
                    if (IsZone(zoneId, "CenterColumn")) return 2;
                    if (IsZone(zoneId, "CenterRightColumn")) return 3;
                    break;
                case PageLayout.WebPart_2010_TwoColumnsLeft:
                    if (IsZone(zoneId, "Left")) return 1;
                    if (IsZone(zoneId, "Right")) return 2;
                    break;
                case PageLayout.WebPart_Custom:
                    return 1;
                default:
                    return 1;
            }

            return 1;
        }

        // Translates the given zone value and page layout to a row number. Ported from WebPartPage.GetRow.
        private static int GetRow(string zoneId, PageLayout layout)
        {
            switch (layout)
            {
                case PageLayout.WebPart_HeaderFooterThreeColumns:
                    if (IsZone(zoneId, "Header")) return 1;
                    if (IsZone(zoneId, "LeftColumn", "MiddleColumn", "RightColumn")) return 2;
                    if (IsZone(zoneId, "Footer")) return 3;
                    break;
                case PageLayout.WebPart_FullPageVertical:
                case PageLayout.WebPart_2010_TwoColumnsLeft:
                    return 1;
                case PageLayout.WebPart_HeaderLeftColumnBody:
                    if (IsZone(zoneId, "Header")) return 1;
                    if (IsZone(zoneId, "LeftColumn", "Body")) return 2;
                    break;
                case PageLayout.WebPart_HeaderRightColumnBody:
                    if (IsZone(zoneId, "Header")) return 1;
                    if (IsZone(zoneId, "RightColumn", "Body")) return 2;
                    break;
                case PageLayout.WebPart_HeaderFooter2Columns4Rows:
                    if (IsZone(zoneId, "Header")) return 1;
                    if (IsZone(zoneId, "LeftColumn", "Row1", "RightColumn", "Row2", "Row3", "Row4")) return 2;
                    if (IsZone(zoneId, "Footer")) return 3;
                    break;
                case PageLayout.WebPart_HeaderFooter4ColumnsTopRow:
                    if (IsZone(zoneId, "Header")) return 1;
                    if (IsZone(zoneId, "LeftColumn", "TopRow", "RightColumn", "CenterLeftColumn", "CenterRightColumn")) return 2;
                    if (IsZone(zoneId, "Footer")) return 3;
                    break;
                case PageLayout.WebPart_LeftColumnHeaderFooterTopRow3Columns:
                    if (IsZone(zoneId, "Header", "LeftColumn")) return 1;
                    if (IsZone(zoneId, "TopRow")) return 2;
                    if (IsZone(zoneId, "CenterLeftColumn", "CenterColumn", "CenterRightColumn")) return 3;
                    if (IsZone(zoneId, "Footer")) return 4;
                    break;
                case PageLayout.WebPart_RightColumnHeaderFooterTopRow3Columns:
                    if (IsZone(zoneId, "Header", "RightColumn")) return 1;
                    if (IsZone(zoneId, "TopRow")) return 2;
                    if (IsZone(zoneId, "CenterLeftColumn", "CenterColumn", "CenterRightColumn")) return 3;
                    if (IsZone(zoneId, "Footer")) return 4;
                    break;
                case PageLayout.WebPart_Custom:
                    return 1;
                default:
                    return 1;
            }

            return 1;
        }

        private static bool IsZone(string zoneId, params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (zoneId.Equals(candidate, StringComparison.InvariantCultureIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
