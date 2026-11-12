using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.Scanners.WebPartMapping;

namespace PnP.Scanning.Core.ClassicPages.Analysis.Page;

/// <summary>Projects captured definitions and page content into ordered detached web-part rows without online dependencies.</summary>
internal static class PageWebPartProjection
{
    // Web part type constants returned by the property-signature based type detection. Ported from
    // the legacy WebParts constants (only the subset GetTypeFromProperties can return is needed).
    private const string XsltListViewType =
        "Microsoft.SharePoint.WebPartPages.XsltListViewWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string ListViewType =
        "Microsoft.SharePoint.WebPartPages.ListViewWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string MediaType =
        "Microsoft.SharePoint.Publishing.WebControls.MediaWebPart, Microsoft.SharePoint.Publishing, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string PictureLibrarySlideshowType =
        "Microsoft.SharePoint.WebPartPages.PictureLibrarySlideshowWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string ChartType =
        "Microsoft.Office.Server.WebControls.ChartWebPart, Microsoft.Office.Server.Chart, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string MembersType =
        "Microsoft.SharePoint.WebPartPages.MembersWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string SilverlightType =
        "Microsoft.SharePoint.WebPartPages.SilverlightWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string ClientType =
        "Microsoft.SharePoint.WebPartPages.ClientWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string ContentEditorType =
        "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string ImageType =
        "Microsoft.SharePoint.WebPartPages.ImageWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string TitleBarType =
        "Microsoft.SharePoint.WebPartPages.TitleBarWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string ScriptEditorType =
        "Microsoft.SharePoint.WebPartPages.ScriptEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string SPUserCodeType =
        "Microsoft.SharePoint.WebPartPages.SPUserCodeWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

    private const string UnsupportedWebPartType = "Unsupported Web Part Type";

    /// <summary>
    /// Builds ordered detached web-part rows from captured page fields, definitions, and original exports.
    /// </summary>
    internal static List<ClassicPageWebPartRow> Build(
        ClassicPageRow page,
        FilePageEvidence source,
        bool exportProperties,
        bool includeTitleBar = false)
    {
        string wiki = source.Fields.GetValueOrDefault("WikiField")?.Text;
        List<WebPartEntity> entities = new List<WebPartEntity>();
        PageLayout layout = PageLayout.WebPart_Custom;
        if (page.PageType == ClassicPageRules.WikiPage)
        {
            WikiContentParseResult parsed = WikiContentParser.Parse(wiki);
            page.Layout = PageLayoutDetector.ToLayoutString(PageLayoutDetector.DetectWikiLayout(wiki));
            entities.AddRange(parsed.TextParts);
            entities.AddRange(parsed.MediaParts);
            foreach (WikiWebPartPlaceholder placeholder in parsed.Placeholders)
            {
                ClassicPageWebPartSource part =
                    source.WebParts.FirstOrDefault(x => x.ControlId == placeholder.ControlId);
                if (part == null) continue;
                WebPartEntity entity = Entity(part);
                entity.ServerControlId = placeholder.Id;
                entity.Row = placeholder.Row;
                entity.Column = placeholder.Column;
                entity.Order = placeholder.Order;
                entity.ZoneId = "";
                entities.Add(entity);
            }

            entities = entities.OrderBy(x => x.Row).ThenBy(x => x.Column).ThenBy(x => x.Order).ToList();
        }
        else
        {
            if (page.PageType == ClassicPageRules.WebPartPage || source.FileLevelOnly)
            {
                layout = PageLayoutDetector.DetectWebPartPageLayout(
                    source.FileProperties.GetValueOrDefault("vti_setuppath")?.Text);
                page.Layout = PageLayoutDetector.ToLayoutString(layout);
            }
            else
            {
                page.Layout = SnapshotPageLayout.GetPublishingPageLayoutName(source.Fields);
            }

            ClassicPageWebPartSource[] parts = page.PageType == ClassicPageRules.PublishingPage
                ? source.WebParts.OrderBy(x => x.ZoneIndex).ToArray()
                : source.WebParts;
            foreach (ClassicPageWebPartSource part in parts)
            {
                if ((page.PageType == ClassicPageRules.WebPartPage || source.FileLevelOnly) && !includeTitleBar &&
                    part.ZoneId.Equals("TitleBar", StringComparison.OrdinalIgnoreCase))
                    continue;
                WebPartEntity entity = Entity(part);
                if (page.PageType == ClassicPageRules.WebPartPage || source.FileLevelOnly)
                {
                    entity.Row = GetRow(part.ZoneId, layout);
                    entity.Column = GetColumn(part.ZoneId, layout);
                }

                entities.Add(entity);
            }
        }

        return ToRows(page, entities, exportProperties);
    }

    /// <summary>
    /// Restores a detached part entity and resolves its type from captured XML or properties.
    /// </summary>
    private static WebPartEntity Entity(ClassicPageWebPartSource part)
    {
        Dictionary<string, object> properties = part.Properties.ToDictionary(x => x.Key, x => x.Value.ToValue());
        return new WebPartEntity
        {
            Id = part.Id,
            ServerControlId = part.Id.ToString(),
            Title = part.Title,
            Type = part.ExportState.Succeeded ? GetTypeFromXml(part.ExportXml) : GetTypeFromProperties(properties),
            ZoneId = part.ZoneId,
            ZoneIndex = (uint)part.ZoneIndex,
            Order = part.ZoneIndex,
            Hidden = part.Hidden,
            IsClosed = part.IsClosed,
            Properties = part.Properties.ToDictionary(
                x => x.Key,
                x => x.Value.Text ?? "",
                StringComparer.InvariantCultureIgnoreCase),
        };
    }

    /// <summary>Copies ordered detached entities into native rows without an intermediate persistence model.</summary>
    private static List<ClassicPageWebPartRow> ToRows(
        ClassicPageRow page,
        List<WebPartEntity> entities,
        bool exportWebPartProperties)
    {
        List<ClassicPageWebPartRow> rows = new List<ClassicPageWebPartRow>();
        int index = 0;
        foreach (WebPartEntity entity in entities)
        {
            rows.Add(
                new ClassicPageWebPartRow
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

    /// <summary>Preserves the existing exported property JSON and its absent-or-empty convention.</summary>
    private static string SerializeProperties(Dictionary<string, string> properties)
    {
        if (properties == null || properties.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(properties);
    }

    /// <summary>Reads the existing v2 or v3 assembly-qualified web-part type from an acquired export.</summary>
    internal static string GetTypeFromXml(string webPartXml)
    {
        string type = "Unknown";

        if (!string.IsNullOrEmpty(webPartXml))
        {
            XElement xml = XElement.Parse(webPartXml);
            XNamespace xmlns = xml.XPathSelectElement("*").GetDefaultNamespace();
            if (xmlns.NamespaceName.Equals(
                    "http://schemas.microsoft.com/WebPart/v3",
                    StringComparison.InvariantCultureIgnoreCase))
            {
                type = xml.Descendants(xmlns + "type").FirstOrDefault()?.Attribute("name")?.Value ?? type;
            }
            else if (xmlns.NamespaceName.Equals(
                         "http://schemas.microsoft.com/WebPart/v2",
                         StringComparison.InvariantCultureIgnoreCase))
            {
                type =
                    $"{xml.Descendants(xmlns + "TypeName").FirstOrDefault()?.Value}, {xml.Descendants(xmlns + "Assembly").FirstOrDefault()?.Value}";
            }
        }

        return type;
    }

    /// <summary>Uses the unchanged property-signature priority when a web-part export was unavailable.</summary>
    internal static string GetTypeFromProperties(IDictionary<string, object> properties)
    {
        if (HasAll(properties, "ListUrl", "ListId", "Xsl", "JSLink", "ShowTimelineIfAvailable"))
            return XsltListViewType;
        if (HasAll(properties, "ListViewXml", "ListName", "ListId", "ViewContentTypeId", "PageType"))
            return ListViewType;
        if (HasAll(
                properties,
                "AutoPlay",
                "MediaSource",
                "Loop",
                "IsPreviewImageSourceOverridenForVideoSet",
                "PreviewImageSource"))
            return MediaType;
        if (HasAll(properties, "LibraryGuid", "Layout", "Speed", "ShowToolbar", "ViewGuid"))
            return PictureLibrarySlideshowType;
        if (HasAll(properties, "ConnectionPointEnabled", "ChartXml", "DataBindingsString", "DesignerChartTheme"))
            return ChartType;
        if (HasAll(properties, "NumberLimit", "DisplayType", "MembershipGroupId", "Toolbar")) return MembersType;
        if (HasAll(properties, "MinRuntimeVersion", "WindowlessMode", "CustomInitParameters", "Url", "ApplicationXml"))
            return SilverlightType;
        if (HasAll(properties, "FeatureId", "ProductWebId", "ProductId")) return ClientType;
        if (HasAll(properties, "Content")) return ScriptEditorType;
        if (HasAll(properties, "CatalogIconImageUrl", "AllowEdit", "TitleIconImageUrl", "ExportMode"))
            return SPUserCodeType;

        return UnsupportedWebPartType;
    }

    /// <summary>Requires every property in one existing web-part signature.</summary>
    private static bool HasAll(IDictionary<string, object> properties, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (!properties.ContainsKey(key))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Maps the captured zone to its legacy layout column, retaining the existing default column.</summary>
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

    /// <summary>Maps the captured zone to its legacy layout row, retaining the existing default row.</summary>
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
                if (IsZone(zoneId, "LeftColumn", "TopRow", "RightColumn", "CenterLeftColumn", "CenterRightColumn"))
                    return 2;
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

    /// <summary>Matches zone identifiers using the existing invariant case-insensitive comparison.</summary>
    private static bool IsZone(string zoneId, params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (zoneId.Equals(candidate, StringComparison.InvariantCultureIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}