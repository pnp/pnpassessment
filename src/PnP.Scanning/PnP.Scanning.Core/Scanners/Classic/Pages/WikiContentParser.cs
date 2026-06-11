using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using System.Text;
using System.Text.RegularExpressions;
using PnP.Scanning.Core.Scanners.WebPartMapping;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Pure (no CSOM) parser of a classic wiki page's <c>WikiField</c> HTML. It walks the wiki layout
    /// table and extracts:
    /// <list type="bullet">
    /// <item><description>the inline <c>WikiText</c> blocks as fully resolved <see cref="WebPartEntity"/> records, and</description></item>
    /// <item><description>placeholders (<see cref="WikiWebPartPlaceholder"/>) for the real web parts embedded in the wiki —
    /// these carry the control id + position only; the actual web part type/title/properties must be
    /// resolved against the live page via CSOM (<c>LimitedWebPartManager</c>), which is deferred to the
    /// page web part extractor / integration test.</description></item>
    /// </list>
    /// Ported from the legacy SharePoint Modernization Scanner
    /// (<c>SharePointPnP.Modernization.Framework\Pages\BasePage.AnalyzeWikiContentBlock</c> +
    /// <c>WikiPage.Analyze</c>), with the CSOM and page-layout detection (a separate task) stripped out so
    /// the HTML parsing is unit-testable on its own.
    /// </summary>
    internal static class WikiContentParser
    {
        // Marker inserted where a web part div used to be, so surrounding text can be split around it.
        private const string WebPartMarkerString = "[[WebPartMarker]]";

        // The fake "type" used for the wiki text blocks; matches the legacy WebParts.WikiText constant so
        // the mapping lookup (which knows this type as a ClientSideText mapping) treats it as mappable.
        internal const string WikiTextPartType = "SharePointPnP.Modernization.WikiTextPart";

        // Pulls the server side control id out of a web part box (e.g. id="div_8c4f...-...." ).
        private static readonly Regex RegexClientIds = new(@"id=\""div_(?<ControlId>(\w|\-)+)", RegexOptions.Compiled);

        /// <summary>
        /// Parses the supplied wiki field HTML into its inline text blocks and embedded web part
        /// placeholders. Never returns null; an empty/whitespace input yields an empty result.
        /// </summary>
        internal static WikiContentParseResult Parse(string wikiFieldHtml)
        {
            var result = new WikiContentParseResult();

            if (string.IsNullOrEmpty(wikiFieldHtml))
            {
                // Empty wiki page: nothing to extract (the legacy scanner would treat this as a single
                // empty one-column layout, but there are no web parts and no text to record).
                return result;
            }

            var parser = new HtmlParser();
            var htmlDoc = parser.ParseDocument(wikiFieldHtml);

            var rows = htmlDoc.All.Where(p => p.LocalName == "tr");
            int rowCount = 0;

            foreach (var row in rows)
            {
                rowCount++;
                var columns = row.Children.Where(p => p.LocalName == "td" && p.Parent == row);

                int colCount = 0;
                foreach (var column in columns)
                {
                    colCount++;
                    var contentHost = column.Children.FirstOrDefault(p => p.LocalName == "div" &&
                        p.ClassName != null && p.ClassName.Equals("ms-rte-layoutszone-outer", StringComparison.InvariantCultureIgnoreCase));

                    // Skip elements nested inside an already processed content host to avoid duplication.
                    if (contentHost != null && contentHost.FirstElementChild != null && !IsNestedLayoutsZoneOuter(contentHost))
                    {
                        var content = contentHost.FirstElementChild;
                        AnalyzeWikiContentBlock(parser, result, htmlDoc, rowCount, colCount, 0, content);
                    }
                }
            }

            // Somehow the wiki was not standard formatted (no layout table we could walk): wrap the whole
            // content in a single text block so the page still records something. Matches the legacy
            // fallback, evaluated here only against the text/placeholders we could extract (the CSOM web
            // part resolution happens later, so we key off "nothing found at all").
            if (result.Placeholders.Count == 0 && result.TextParts.Count == 0)
            {
                result.TextParts.Add(CreateWikiTextPart(wikiFieldHtml, 1, 1, 1));
            }

            return result;
        }

        // Ported from BasePage.AnalyzeWikiContentBlock — walks one content host's child nodes, emitting
        // interleaved WikiText parts and web part placeholders in document order.
        private static void AnalyzeWikiContentBlock(HtmlParser parser, WikiContentParseResult result, IHtmlDocument htmlDoc,
                                                    int rowCount, int colCount, int startOrder, IElement content)
        {
            // Drop elements which we anyhow can't transform and/or which are stripped out from RTE.
            CleanHtml(content, htmlDoc);

            StringBuilder textContent = new();
            int order = startOrder;
            foreach (var node in content.ChildNodes)
            {
                // Do we find a web part inside...
                if (node is IHtmlElement htmlElement && ContainsWebPart(parser, htmlElement))
                {
                    var extraText = StripWebPart(parser, htmlElement);
                    string extraTextAfterWebPart = null;
                    string extraTextBeforeWebPart = null;
                    if (!string.IsNullOrEmpty(extraText))
                    {
                        // Should be, but checking anyhow
                        int webPartMarker = extraText.IndexOf(WebPartMarkerString);
                        if (webPartMarker > -1)
                        {
                            extraTextBeforeWebPart = extraText.Substring(0, webPartMarker);
                            extraTextAfterWebPart = extraText.Substring(webPartMarker + WebPartMarkerString.Length);

                            // there could have been multiple web parts in a row (we don't support text
                            // inbetween them for now)...strip the remaining markers
                            extraTextBeforeWebPart = extraTextBeforeWebPart.Replace(WebPartMarkerString, "");
                            extraTextAfterWebPart = extraTextAfterWebPart.Replace(WebPartMarkerString, "");
                        }
                    }

                    if (!string.IsNullOrEmpty(extraTextBeforeWebPart))
                    {
                        textContent.AppendLine(extraTextBeforeWebPart);
                    }

                    // first insert text part (if it was available)
                    if (!string.IsNullOrEmpty(textContent.ToString()))
                    {
                        order++;
                        result.TextParts.Add(CreateWikiTextPart(textContent.ToString(), rowCount, colCount, order));
                        textContent.Clear();
                    }

                    // then process the web part
                    order++;
                    if (RegexClientIds.IsMatch(htmlElement.OuterHtml))
                    {
                        foreach (Match webPartMatch in RegexClientIds.Matches(htmlElement.OuterHtml))
                        {
                            // Store the web part we need, will be retrieved afterwards via CSOM.
                            string serverSideControlId = webPartMatch.Groups["ControlId"].Value;
                            var serverSideControlIdToSearchFor = $"g_{serverSideControlId.Replace("-", "_")}";
                            result.Placeholders.Add(new WikiWebPartPlaceholder
                            {
                                ControlId = serverSideControlIdToSearchFor,
                                Id = serverSideControlId,
                                Row = rowCount,
                                Column = colCount,
                                Order = order,
                            });
                        }
                    }

                    // Process the extra text that was positioned after the web part (if any)
                    if (!string.IsNullOrEmpty(extraTextAfterWebPart))
                    {
                        textContent.AppendLine(extraTextAfterWebPart);
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(node.TextContent.Trim()) && node.TextContent.Trim() == "\n")
                    {
                        // ignore, this one is typically added after a web part
                    }
                    else
                    {
                        if (node.HasChildNodes)
                        {
                            textContent.AppendLine((node as IHtmlElement)?.OuterHtml);
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(node.TextContent.Trim()))
                            {
                                textContent.AppendLine(node.TextContent);
                            }
                            else
                            {
                                if (node.NodeName.Equals("br", StringComparison.InvariantCultureIgnoreCase))
                                {
                                    textContent.AppendLine("<BR>");
                                }
                                // given that wiki html can contain embedded images and videos while not
                                // having child nodes we need include these.
                                else if (node.NodeName.Equals("img", StringComparison.InvariantCultureIgnoreCase) ||
                                         node.NodeName.Equals("iframe", StringComparison.InvariantCultureIgnoreCase))
                                {
                                    textContent.AppendLine((node as IHtmlElement)?.OuterHtml);
                                }
                            }
                        }
                    }
                }
            }

            // there was only one text part
            if (!string.IsNullOrEmpty(textContent.ToString()))
            {
                // insert text part to the web part collection
                order++;
                result.TextParts.Add(CreateWikiTextPart(textContent.ToString(), rowCount, colCount, order));
            }
        }

        /// <summary>
        /// Check if this element is nested in another already processed element...this needs to be
        /// skipped to avoid content duplication and possible processing errors.
        /// </summary>
        private static bool IsNestedLayoutsZoneOuter(IElement contentHost)
        {
            var elementToInspect = contentHost?.ParentElement;

            while (elementToInspect != null)
            {
                if (elementToInspect.LocalName == "div" && elementToInspect.ClassName != null &&
                    elementToInspect.ClassName.Equals("ms-rte-layoutszone-outer", StringComparison.InvariantCultureIgnoreCase))
                {
                    return true;
                }

                elementToInspect = elementToInspect.ParentElement;
            }

            return false;
        }

        // Stores text content as a fake web part. Ported from BasePage.CreateWikiTextPart.
        private static WebPartEntity CreateWikiTextPart(string wikiTextPartContent, int row, int col, int order)
        {
            return new WebPartEntity
            {
                Title = "WikiText",
                Type = WikiTextPartType,
                Id = Guid.Empty,
                Row = row,
                Column = col,
                Order = order,
                Properties = new Dictionary<string, string>
                {
                    { "Text", wikiTextPartContent.Trim().Replace("\r\n", string.Empty) }
                },
            };
        }

        private static void CleanHtml(IElement element, IHtmlDocument document)
        {
            foreach (var node in element.QuerySelectorAll("*").ToList())
            {
                if (node.ParentElement != null && IsUntransformableBlockElement(node))
                {
                    // create new div node and add all current children to it
                    var div = document.CreateElement("div");
                    foreach (var child in node.ChildNodes.ToList())
                    {
                        div.AppendChild(child);
                    }
                    // replace the unsupported node with the new div
                    node.ParentElement.ReplaceChild(div, node);
                }
            }
        }

        private static bool IsUntransformableBlockElement(IElement element)
        {
            var tag = element.TagName.ToLower();
            switch (tag)
            {
                case "article":
                case "address":
                case "aside":
                case "canvas":
                case "dd":
                case "dl":
                case "dt":
                case "fieldset":
                case "figcaption":
                case "figure":
                case "footer":
                case "form":
                case "header":
                case "main":
                case "nav":
                case "noscript":
                case "output":
                case "pre":
                case "section":
                case "tfoot":
                case "video":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Does the tree of nodes somewhere contain a web part?
        /// </summary>
        private static bool ContainsWebPart(HtmlParser parser, IHtmlElement element)
        {
            var doc = parser.ParseDocument(element.OuterHtml);
            var nodes = doc.All.Where(p => p.LocalName == "div");
            foreach (var node in nodes)
            {
                if (node is IHtmlElement htmlNode && htmlNode.ClassList.Contains("ms-rte-wpbox"))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Strips the div holding the web part from the html, replacing it with a marker so the
        /// surrounding text can be recovered.
        /// </summary>
        private static string StripWebPart(HtmlParser parser, IHtmlElement element)
        {
            IElement copy = element.Clone(true) as IElement;
            var doc = parser.ParseDocument(copy.OuterHtml);
            var nodes = doc.All.Where(p => p.LocalName == "div");
            if (nodes.Any())
            {
                foreach (var node in nodes.ToList())
                {
                    if (node is IHtmlElement htmlNode && htmlNode.ClassList.Contains("ms-rte-wpbox"))
                    {
                        var newElement = doc.CreateTextNode(WebPartMarkerString);
                        node.Parent.ReplaceChild(newElement, node);
                    }
                }

                if (doc.DocumentElement.Children[1].FirstElementChild != null &&
                    doc.DocumentElement.Children[1].FirstElementChild is IHtmlDivElement)
                {
                    return doc.DocumentElement.Children[1].FirstElementChild.InnerHtml;
                }
                else
                {
                    return doc.DocumentElement.Children[1].InnerHtml;
                }
            }
            else
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Result of parsing a wiki page's HTML: the inline text blocks and the placeholders for the
    /// embedded web parts (which still need a CSOM round-trip to resolve their type/properties).
    /// </summary>
    internal sealed class WikiContentParseResult
    {
        /// <summary>
        /// Placeholders for the real web parts embedded in the wiki content, in document order.
        /// </summary>
        public List<WikiWebPartPlaceholder> Placeholders { get; } = new();

        /// <summary>
        /// The inline wiki text blocks, already fully resolved as <see cref="WebPartEntity"/> records.
        /// </summary>
        public List<WebPartEntity> TextParts { get; } = new();
    }

    /// <summary>
    /// A reference to a web part embedded in wiki content. Carries the position and the server side
    /// control id needed to look the web part up via <c>LimitedWebPartManager.WebParts.GetByControlId</c>.
    /// </summary>
    internal sealed class WikiWebPartPlaceholder
    {
        /// <summary>Raw control id as found in the page HTML (e.g. the web part definition guid).</summary>
        public string Id { get; set; }

        /// <summary>Control id in the <c>g_xxxx</c> form used by <c>GetByControlId</c>.</summary>
        public string ControlId { get; set; }

        /// <summary>Wiki layout row (1-based).</summary>
        public int Row { get; set; }

        /// <summary>Wiki layout column (1-based).</summary>
        public int Column { get; set; }

        /// <summary>Order of the web part within its row/column.</summary>
        public int Order { get; set; }
    }
}
