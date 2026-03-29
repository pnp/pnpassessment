using System.Xml.Linq;
using System.Xml.XPath;
using PnP.Scanning.Core.Scanners.Extensions;

namespace PnP.Scanning.Core.Scanners;

public class WebPartHelper
{
    
    public static string GetType(string webPartXml)
    {
        string type = "Unknown";

        if (!string.IsNullOrEmpty(webPartXml))
        {
            var xml = XElement.Parse(webPartXml);
            var xmlns = xml.XPathSelectElement("*").GetDefaultNamespace();
            if (xmlns.NamespaceName.Equals("http://schemas.microsoft.com/WebPart/v3", StringComparison.InvariantCultureIgnoreCase))
            {
                type = xml.Descendants(xmlns + "type").FirstOrDefault().Attribute("name").Value;
            }
            else if (xmlns.NamespaceName.Equals("http://schemas.microsoft.com/WebPart/v2", StringComparison.InvariantCultureIgnoreCase))
            {
                type = $"{xml.Descendants(xmlns + "TypeName").FirstOrDefault().Value}, {xml.Descendants(xmlns + "Assembly").FirstOrDefault().Value}";
            }
        }

        return type;
    }

    public static string GetTypeFromProperties(Dictionary<string, object> properties, bool isLegacy = false)
    {
        // Check for XSLTListView web part
        string[] xsltWebPart = new string[] { "ListUrl", "ListId", "Xsl", "JSLink", "ShowTimelineIfAvailable" };
        if (CheckWebPartProperties(xsltWebPart, properties))
        {
            return WebParts.XsltListView;
        }

        // Check for ListView web part
        string[] listWebPart = new string[] { "ListViewXml", "ListName", "ListId", "ViewContentTypeId", "PageType" };
        if (CheckWebPartProperties(listWebPart, properties))
        {
            return WebParts.ListView;
        }

        // check for Media web part
        string[] mediaWebPart = new string[] { "AutoPlay", "MediaSource", "Loop", "IsPreviewImageSourceOverridenForVideoSet", "PreviewImageSource" };
        if (CheckWebPartProperties(mediaWebPart, properties))
        {
            return WebParts.Media;
        }

        // check for SlideShow web part
        string[] slideShowWebPart = new string[] { "LibraryGuid", "Layout", "Speed", "ShowToolbar", "ViewGuid" };
        if (CheckWebPartProperties(slideShowWebPart, properties))
        {
            return WebParts.PictureLibrarySlideshow;
        }

        // check for Chart web part
        string[] chartWebPart = new string[] { "ConnectionPointEnabled", "ChartXml", "DataBindingsString", "DesignerChartTheme" };
        if (CheckWebPartProperties(chartWebPart, properties))
        {
            return WebParts.Chart;
        }

        // check for Site Members web part
        string[] membersWebPart = new string[] { "NumberLimit", "DisplayType", "MembershipGroupId", "Toolbar" };
        if (CheckWebPartProperties(membersWebPart, properties))
        {
            return WebParts.Members;
        }

        // check for Silverlight web part
        string[] silverlightWebPart = new string[] { "MinRuntimeVersion", "WindowlessMode", "CustomInitParameters", "Url", "ApplicationXml" };
        if (CheckWebPartProperties(silverlightWebPart, properties))
        {
            return WebParts.Silverlight;
        }

        // check for Add-in Part web part
        string[] addinPartWebPart = new string[] { "FeatureId", "ProductWebId", "ProductId" };
        if (CheckWebPartProperties(addinPartWebPart, properties))
        {
            return WebParts.Client;
        }

        if (isLegacy)
        {
            // Content Editor Web Part
            string[] contentEditorWebPart = new string[] { "Content", "ContentLink", "PartStorage" };
            if (CheckWebPartProperties(contentEditorWebPart, properties))
            {
                return WebParts.ContentEditor;
            }

            // Image Viewer Web Part
            string[] imageViewerWebPart = new string[] { "ImageLink", "AlternativeText", "VerticalAlignment", "HorizontalAlignment" };
            if (CheckWebPartProperties(imageViewerWebPart, properties))
            {
                return WebParts.Image;
            }

            // Title Bar 
            if(properties.ContainsKey("TypeName") && properties["TypeName"].ToString() == "Microsoft.SharePoint.WebPartPages.TitleBarWebPart")
            {
                return WebParts.TitleBar;
            }

            // Check for ListView web part
            string[] legacyListWebPart = new string[] { "ListViewXml", "ListName", "ListId", "ViewContentTypeId" };
            if (CheckWebPartProperties(legacyListWebPart, properties))
            {
                return WebParts.ListView;
            }

            string[] legacyXsltWebPart = new string[] { "ListUrl", "ListId", "ListName", "CatalogIconImageUrl" };
            if (CheckWebPartProperties(legacyXsltWebPart, properties))
            {
                // Too Many Lists are showing here, so extra filters are required
                // Not the cleanest method, but options limited to filter list type without extra calls to SharePoint
                var iconsToCheck = new string[]{
                "images/itdl.png", "images/itissue.png", "images/itgen.png" };
                var iconToRepresent = properties["CatalogIconImageUrl"];
                foreach(var iconPath in iconsToCheck)
                {
                    if (iconToRepresent.ToString().ContainsIgnoringCasing(iconPath))
                    {
                        return WebParts.XsltListView;
                    }
                }
            }
        }

        // check for Script Editor web part
        string[] scriptEditorWebPart = new string[] { "Content" };
        if (CheckWebPartProperties(scriptEditorWebPart, properties))
        {
            return WebParts.ScriptEditor;
        }

        // This needs to be last, but we still pages with sandbox user code web parts on them
        string[] sandboxWebPart = new string[] { "CatalogIconImageUrl", "AllowEdit", "TitleIconImageUrl", "ExportMode" };
        if (CheckWebPartProperties(sandboxWebPart, properties))
        {
            return WebParts.SPUserCode;
        }

        return "Unsupported Web Part Type";
    }
    
    private static bool CheckWebPartProperties(string[] propertiesToCheck, Dictionary<string, object> properties)
    {
        bool isWebPart = true;
        foreach (var wpProp in propertiesToCheck)
        {
            if (!properties.ContainsKey(wpProp))
            {
                isWebPart = false;
                break;
            }
        }

        return isWebPart;
    }
    
}