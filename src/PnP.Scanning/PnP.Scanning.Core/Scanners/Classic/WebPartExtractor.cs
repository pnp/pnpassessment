using Microsoft.SharePoint.Client;
using PnP.Scanning.Core.Storage;
using System.Text.Json;
using System.Xml;
using Serilog;
using System.Text.RegularExpressions;

namespace PnP.Scanning.Core.Scanners.Classic
{
    internal static class WebPartExtractor
    {
        /// <summary>
        /// Extracts web parts from a SharePoint page
        /// </summary>
        /// <param name="scanId">Scan ID</param>
        /// <param name="siteUrl">Site collection URL</param>
        /// <param name="webUrl">Web URL</param>
        /// <param name="pageUrl">Page URL</param>
        /// <param name="pageName">Page name</param>
        /// <param name="listId">List ID where the page is stored</param>
        /// <param name="modifiedAt">Page modification date</param>
        /// <param name="csomContext">CSOM context</param>
        /// <returns>List of ClassicWebPart entities</returns>
        internal static async Task<List<ClassicWebPart>> ExtractWebPartsFromPageAsync(
            Guid scanId, 
            string siteUrl, 
            string webUrl, 
            string pageUrl, 
            string pageName, 
            Guid listId, 
            DateTime modifiedAt,
            ClientContext csomContext)
        {
            var webParts = new List<ClassicWebPart>();

            try
            {
                // Get the page file
                var file = csomContext.Web.GetFileByServerRelativeUrl(pageUrl);
                csomContext.Load(file);
                await csomContext.ExecuteQueryAsync();

                // Check if this is a web part page
                if (file.Name.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
                {
                    // Get limited web part manager to enumerate web parts
                    var limitedWebPartManager = file.GetLimitedWebPartManager(Microsoft.SharePoint.Client.WebParts.PersonalizationScope.Shared);
                    csomContext.Load(limitedWebPartManager.WebParts, 
                        wps => wps.Include(
                            wp => wp.Id,
                            wp => wp.WebPart.Title,
                            wp => wp.WebPart.ZoneIndex,
                            wp => wp.WebPart.IsClosed,
                            wp => wp.WebPart.Hidden,
                            wp => wp.WebPart.Properties));

                    await csomContext.ExecuteQueryAsync();

                    foreach (var webPartDefinition in limitedWebPartManager.WebParts)
                    {
                        try
                        {
                            var webPart = webPartDefinition.WebPart;
                            
                            // Extract web part type information
                            string webPartType = ExtractWebPartType(webPart);
                            string webPartAssembly = ExtractWebPartAssembly(webPart);
                            string webPartClass = ExtractWebPartClass(webPart);
                            
                            // Check if this web part has proper mapping
                            bool hasProperMapping = !Constants.ClassicWebPartsWithoutProperMappings.Contains(webPartType);
                            
                            // Determine remediation code
                            string remediationCode = DetermineRemediationCode(webPartType, hasProperMapping);

                            // Serialize web part properties for storage
                            string propertiesJson = SerializeWebPartProperties(webPart);

                            var classicWebPart = new ClassicWebPart
                            {
                                ScanId = scanId,
                                SiteUrl = siteUrl,
                                WebUrl = webUrl,
                                PageUrl = pageUrl,
                                PageName = pageName,
                                WebPartId = webPartDefinition.Id.ToString(),
                                WebPartType = webPartType,
                                WebPartTitle = webPart.Title ?? string.Empty,
                                WebPartZone = webPart.ZoneIndex,
                                WebPartZoneIndex = webPart.ZoneIndex,
                                WebPartProperties = propertiesJson,
                                IsClosed = webPart.IsClosed,
                                IsHidden = webPart.Hidden,
                                WebPartAssembly = webPartAssembly,
                                WebPartClass = webPartClass,
                                HasProperMapping = hasProperMapping,
                                RemediationCode = remediationCode,
                                ListId = listId,
                                ModifiedAt = modifiedAt
                            };

                            webParts.Add(classicWebPart);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning("Failed to extract web part details from page {PageUrl}: {Error}", pageUrl, ex.Message);
                        }
                    }
                }
                else if (pageUrl.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
                {
                    // For wiki pages and publishing pages, we need to parse the content differently
                    await ExtractWebPartsFromWikiOrPublishingPageAsync(scanId, siteUrl, webUrl, pageUrl, pageName, listId, modifiedAt, csomContext, webParts);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to extract web parts from page {PageUrl}: {Error}", pageUrl, ex.Message);
            }

            return webParts;
        }

        private static async Task ExtractWebPartsFromWikiOrPublishingPageAsync(
            Guid scanId, 
            string siteUrl, 
            string webUrl, 
            string pageUrl, 
            string pageName, 
            Guid listId, 
            DateTime modifiedAt, 
            ClientContext csomContext, 
            List<ClassicWebPart> webParts)
        {
            try
            {
                // Get the list item to access the content
                var file = csomContext.Web.GetFileByServerRelativeUrl(pageUrl);
                var listItem = file.ListItemAllFields;
                csomContext.Load(listItem);
                csomContext.Load(file);
                await csomContext.ExecuteQueryAsync();

                // Extract web parts from wiki content or publishing page content
                string content = string.Empty;
                
                // Try to get wiki field content
                if (listItem.FieldValues.ContainsKey("WikiField"))
                {
                    content = listItem["WikiField"]?.ToString() ?? string.Empty;
                }
                // Try to get publishing page content
                else if (listItem.FieldValues.ContainsKey("PublishingPageContent"))
                {
                    content = listItem["PublishingPageContent"]?.ToString() ?? string.Empty;
                }

                if (!string.IsNullOrEmpty(content))
                {
                    await ExtractWebPartsFromHtmlContentAsync(scanId, siteUrl, webUrl, pageUrl, pageName, listId, modifiedAt, content, webParts);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to extract web parts from wiki/publishing page {PageUrl}: {Error}", pageUrl, ex.Message);
            }
        }

        private static async Task ExtractWebPartsFromHtmlContentAsync(
            Guid scanId, 
            string siteUrl, 
            string webUrl, 
            string pageUrl, 
            string pageName, 
            Guid listId, 
            DateTime modifiedAt, 
            string content, 
            List<ClassicWebPart> webParts)
        {
            // Pattern to match embedded web parts in wiki/publishing content
            var webPartPattern = new Regex(@"<\s*WebPartPages:WebPartZone[^>]*>.*?<\s*WebPartPages:WebPart[^>]*>.*?</\s*WebPartPages:WebPart\s*>.*?</\s*WebPartPages:WebPartZone\s*>", 
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var matches = webPartPattern.Matches(content);

            for (int i = 0; i < matches.Count; i++)
            {
                try
                {
                    var match = matches[i];
                    var webPartXml = match.Value;

                    // Parse the web part XML to extract information
                    var webPartInfo = ParseWebPartXml(webPartXml);
                    if (webPartInfo != null)
                    {
                        bool hasProperMapping = !Constants.ClassicWebPartsWithoutProperMappings.Contains(webPartInfo.Type);
                        string remediationCode = DetermineRemediationCode(webPartInfo.Type, hasProperMapping);

                        var classicWebPart = new ClassicWebPart
                        {
                            ScanId = scanId,
                            SiteUrl = siteUrl,
                            WebUrl = webUrl,
                            PageUrl = pageUrl,
                            PageName = pageName,
                            WebPartId = webPartInfo.Id ?? $"embedded_{i}",
                            WebPartType = webPartInfo.Type,
                            WebPartTitle = webPartInfo.Title ?? string.Empty,
                            WebPartZoneIndex = webPartInfo.ZoneIndex,
                            WebPartProperties = webPartInfo.Properties ?? string.Empty,
                            IsClosed = false,
                            IsHidden = false,
                            WebPartAssembly = webPartInfo.Assembly ?? string.Empty,
                            WebPartClass = webPartInfo.Class ?? string.Empty,
                            HasProperMapping = hasProperMapping,
                            RemediationCode = remediationCode,
                            ListId = listId,
                            ModifiedAt = modifiedAt
                        };

                        webParts.Add(classicWebPart);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("Failed to parse embedded web part in page {PageUrl}: {Error}", pageUrl, ex.Message);
                }
            }
        }

        private static WebPartInfo ParseWebPartXml(string webPartXml)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(webPartXml);

                var webPartInfo = new WebPartInfo();

                // Extract type information from the XML
                var typeNodes = doc.SelectNodes("//@__WebPartType");
                if (typeNodes?.Count > 0)
                {
                    webPartInfo.Type = typeNodes[0]?.Value;
                }

                // Extract other properties
                var titleNodes = doc.SelectNodes("//property[@name='Title']");
                if (titleNodes?.Count > 0)
                {
                    webPartInfo.Title = titleNodes[0]?.InnerText;
                }

                // Extract zone information if available
                var zoneNodes = doc.SelectNodes("//WebPartPages:WebPartZone/@id");
                if (zoneNodes?.Count > 0)
                {
                    webPartInfo.Zone = zoneNodes[0]?.Value;
                }

                // Serialize all properties for storage
                webPartInfo.Properties = webPartXml;

                return webPartInfo;
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to parse web part XML: {Error}", ex.Message);
                return null;
            }
        }

        private static string ExtractWebPartType(Microsoft.SharePoint.Client.WebParts.WebPart webPart)
        {
            try
            {
                // Try to get the type from web part properties
                if (webPart.Properties.FieldValues.ContainsKey("__WebPartType"))
                {
                    return webPart.Properties["__WebPartType"]?.ToString();
                }

                // Fallback to trying other type indicators
                return webPart.GetType().FullName;
            }
            catch
            {
                return "Unknown";
            }
        }

        private static string ExtractWebPartAssembly(Microsoft.SharePoint.Client.WebParts.WebPart webPart)
        {
            try
            {
                if (webPart.Properties.FieldValues.ContainsKey("__WebPartAssembly"))
                {
                    return webPart.Properties["__WebPartAssembly"]?.ToString();
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ExtractWebPartClass(Microsoft.SharePoint.Client.WebParts.WebPart webPart)
        {
            try
            {
                if (webPart.Properties.FieldValues.ContainsKey("__WebPartClass"))
                {
                    return webPart.Properties["__WebPartClass"]?.ToString();
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SerializeWebPartProperties(Microsoft.SharePoint.Client.WebParts.WebPart webPart)
        {
            try
            {
                var properties = new Dictionary<string, object>();
                
                if (webPart.Properties?.FieldValues != null)
                {
                    foreach (var property in webPart.Properties.FieldValues)
                    {
                        properties[property.Key] = property.Value;
                    }
                }

                return JsonSerializer.Serialize(properties, new JsonSerializerOptions 
                { 
                    WriteIndented = false,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to serialize web part properties: {Error}", ex.Message);
                return "{}";
            }
        }

        private static string DetermineRemediationCode(string webPartType, bool hasProperMapping)
        {
            if (string.IsNullOrEmpty(webPartType))
            {
                return nameof(RemediationCodes.WP3); // Unknown web part type
            }

            if (!hasProperMapping)
            {
                return nameof(RemediationCodes.WP1);
            }

            // Web parts with mapping but might need attention
            if (webPartType.Contains("Script", StringComparison.OrdinalIgnoreCase) ||
                webPartType.Contains("ContentEditor", StringComparison.OrdinalIgnoreCase))
            {
                return nameof(RemediationCodes.WP2); // Script-based web parts requiring community script editor
            }

            return string.Empty; // No remediation needed
        }

        private class WebPartInfo
        {
            public string Id { get; set; }
            public string Type { get; set; }
            public string Title { get; set; }
            public string Zone { get; set; }
            public int ZoneIndex { get; set; }
            public string Properties { get; set; }
            public string Assembly { get; set; }
            public string Class { get; set; }
        }
    }
}
