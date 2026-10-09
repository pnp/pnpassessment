using PnP.Scanning.Core.Scanners;
using ClassicPage = PnP.Scanning.Core.Pipeline.Contracts.ClassicPageRow;
using ClassicPageWebPart = PnP.Scanning.Core.Pipeline.Contracts.ClassicPageWebPartRow;
using Web = PnP.Scanning.Core.Pipeline.Contracts.WebRow;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;

using PnP.Scanning.Core.Scanners.WebPartMapping;
using PnP.Scanning.Core.Pipeline.Contracts;
namespace PnP.Scanning.Core.Pipeline.Analysis;

internal static partial class PageWebPartAnalysis
{
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
}
