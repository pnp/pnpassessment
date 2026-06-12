using System;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using PnP.Scanning.Core.Scanners.WebPartMapping;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
    /// Derives the <see cref="PageLayout"/> of a classic page from its structure, and renders that layout
    /// to the short string stored on <c>ClassicPage.Layout</c>. Three sources, mirroring the legacy
    /// Modernization Scanner:
    /// <list type="bullet">
    /// <item><description><b>Wiki pages</b> — parsed from the <c>WikiField</c> HTML (the <c>layoutsdata</c>
    /// span plus a column-count fallback). Ported from <c>WikiPage.GetLayout</c>; pure, no CSOM.</description></item>
    /// <item><description><b>Web part pages</b> — derived from the page file's <c>vti_setuppath</c>
    /// property. Ported from <c>WebPartPage.GetLayout</c>; the pure string→layout mapping lives here so it
    /// is unit-testable (the CSOM property read stays in <see cref="PageWebPartExtractor"/>).</description></item>
    /// </list>
    /// The string form matches the legacy page CSV: the enum name with the <c>Wiki_</c> / <c>WebPart_</c>
    /// prefix stripped (e.g. <c>Wiki_TwoColumns</c> → <c>TwoColumns</c>, <c>WebPart_Custom</c> →
    /// <c>Custom</c>).
    /// <para>
    /// <b>Publishing pages</b> are handled differently and not here: the legacy <c>PublishingAnalyzer</c>
    /// records the actual page layout <i>name</i> (e.g. <c>ArticleLeft</c>) from the page's
    /// <c>PublishingPageLayout</c> field, not a derived <see cref="PageLayout"/> enum value. That field read
    /// lives in <see cref="PageWebPartExtractor.GetPublishingPageLayoutName"/>.
    /// </para>
    /// </summary>
    internal static class PageLayoutDetector
    {
        /// <summary>
        /// Detects the wiki page layout from its <c>WikiField</c> HTML. An empty/whitespace page is a
        /// one-column layout (parity with the legacy scanner). Ported verbatim from
        /// <c>WikiPage.GetLayout</c>: read the <c>layoutsdata</c> span, then fall back to counting the
        /// layout-table columns for pages (e.g. community-template pages) that omit a recognized span value.
        /// </summary>
        internal static PageLayout DetectWikiLayout(string wikiFieldHtml)
        {
            if (string.IsNullOrEmpty(wikiFieldHtml))
            {
                return PageLayout.Wiki_OneColumn;
            }

            var parser = new HtmlParser();
            var doc = parser.ParseDocument(wikiFieldHtml);

            string spanValue = "";
            var spanTags = doc.All.Where(p => p.LocalName == "span" && p.HasAttribute("id"));
            foreach (var span in spanTags)
            {
                if (span.GetAttribute("id").Equals("layoutsdata", StringComparison.InvariantCultureIgnoreCase))
                {
                    spanValue = span.InnerHtml.ToLower();

                    if (spanValue == "false,false,1")
                    {
                        return PageLayout.Wiki_OneColumn;
                    }
                    else if (spanValue == "false,false,2")
                    {
                        var tdTag = doc.All.FirstOrDefault(p => p.LocalName == "td" && p.HasAttribute("style"));
                        if (tdTag != null)
                        {
                            if (tdTag.GetAttribute("style").IndexOf("width:49.95%;", StringComparison.InvariantCultureIgnoreCase) > -1)
                            {
                                return PageLayout.Wiki_TwoColumns;
                            }
                            else if (tdTag.GetAttribute("style").IndexOf("width:66.6%;", StringComparison.InvariantCultureIgnoreCase) > -1)
                            {
                                return PageLayout.Wiki_TwoColumnsWithSidebar;
                            }
                            else
                            {
                                return PageLayout.Wiki_TwoColumns;
                            }
                        }
                    }
                    else if (spanValue == "true,false,2")
                    {
                        return PageLayout.Wiki_TwoColumnsWithHeader;
                    }
                    else if (spanValue == "true,true,2")
                    {
                        return PageLayout.Wiki_TwoColumnsWithHeaderAndFooter;
                    }
                    else if (spanValue == "false,false,3")
                    {
                        return PageLayout.Wiki_ThreeColumns;
                    }
                    else if (spanValue == "true,false,3")
                    {
                        return PageLayout.Wiki_ThreeColumnsWithHeader;
                    }
                    else if (spanValue == "true,true,3")
                    {
                        return PageLayout.Wiki_ThreeColumnsWithHeaderAndFooter;
                    }
                }
            }

            // Oops, we're still here...let's try to deduct a layout as some pages (e.g. from community
            // template) do not add the proper span value.
            if (spanValue.StartsWith("false,false,") || spanValue.StartsWith("true,true,") || spanValue.StartsWith("true,false,"))
            {
                var tdTags = doc.All.Where(p => p.LocalName == "td" && p.HasAttribute("style"));
                if (spanValue.StartsWith("false,false,"))
                {
                    if (tdTags.Count() == 1)
                    {
                        return PageLayout.Wiki_OneColumn;
                    }
                    else if (tdTags.Count() == 2)
                    {
                        if (tdTags.First().GetAttribute("style").IndexOf("width:49.95%;", StringComparison.InvariantCultureIgnoreCase) > -1)
                        {
                            return PageLayout.Wiki_TwoColumns;
                        }
                        else if (tdTags.First().GetAttribute("style").IndexOf("width:66.6%;", StringComparison.InvariantCultureIgnoreCase) > -1)
                        {
                            return PageLayout.Wiki_TwoColumnsWithSidebar;
                        }
                        else
                        {
                            return PageLayout.Wiki_TwoColumns;
                        }
                    }
                    else if (tdTags.Count() == 3)
                    {
                        return PageLayout.Wiki_ThreeColumns;
                    }
                }
                else if (spanValue.StartsWith("true,true,"))
                {
                    if (tdTags.Count() == 2)
                    {
                        return PageLayout.Wiki_TwoColumnsWithHeaderAndFooter;
                    }
                    else if (tdTags.Count() == 3)
                    {
                        return PageLayout.Wiki_ThreeColumnsWithHeaderAndFooter;
                    }
                }
                else if (spanValue.StartsWith("true,false,"))
                {
                    if (tdTags.Count() == 2)
                    {
                        return PageLayout.Wiki_TwoColumnsWithHeader;
                    }
                    else if (tdTags.Count() == 3)
                    {
                        return PageLayout.Wiki_ThreeColumnsWithHeader;
                    }
                }
            }

            return PageLayout.Wiki_Custom;
        }

        /// <summary>
        /// Detects the web part page layout from the page file's <c>vti_setuppath</c> value. Ported from
        /// <c>WebPartPage.GetLayout</c>: each built-in smart-page template maps to a known multi-zone
        /// layout; everything else (including a null/empty setup path) is a custom layout.
        /// </summary>
        /// <param name="setupPath">The <c>vti_setuppath</c> page property value, or null if absent.</param>
        internal static PageLayout DetectWebPartPageLayout(string setupPath)
        {
            if (!string.IsNullOrEmpty(setupPath))
            {
                if (setupPath.IndexOf(@"\STS\doctemp\smartpgs\spstd1.aspx", StringComparison.InvariantCultureIgnoreCase) > -1) return PageLayout.WebPart_FullPageVertical;
                if (setupPath.IndexOf(@"\STS\doctemp\smartpgs\spstd2.aspx", StringComparison.InvariantCultureIgnoreCase) > -1) return PageLayout.WebPart_HeaderFooterThreeColumns;
                if (setupPath.IndexOf(@"\STS\doctemp\smartpgs\spstd3.aspx", StringComparison.InvariantCultureIgnoreCase) > -1) return PageLayout.WebPart_HeaderLeftColumnBody;
                if (setupPath.IndexOf(@"\STS\doctemp\smartpgs\spstd4.aspx", StringComparison.InvariantCultureIgnoreCase) > -1) return PageLayout.WebPart_HeaderRightColumnBody;
                if (setupPath.IndexOf(@"\STS\doctemp\smartpgs\spstd5.aspx", StringComparison.InvariantCultureIgnoreCase) > -1) return PageLayout.WebPart_HeaderFooter2Columns4Rows;
                if (setupPath.IndexOf(@"\STS\doctemp\smartpgs\spstd6.aspx", StringComparison.InvariantCultureIgnoreCase) > -1) return PageLayout.WebPart_HeaderFooter4ColumnsTopRow;
                if (setupPath.IndexOf(@"\STS\doctemp\smartpgs\spstd7.aspx", StringComparison.InvariantCultureIgnoreCase) > -1) return PageLayout.WebPart_LeftColumnHeaderFooterTopRow3Columns;
                if (setupPath.IndexOf(@"\STS\doctemp\smartpgs\spstd8.aspx", StringComparison.InvariantCultureIgnoreCase) > -1) return PageLayout.WebPart_RightColumnHeaderFooterTopRow3Columns;
                if (setupPath.Equals(@"SiteTemplates\STS\default.aspx", StringComparison.InvariantCultureIgnoreCase)) return PageLayout.WebPart_2010_TwoColumnsLeft;
            }

            return PageLayout.WebPart_Custom;
        }

        /// <summary>
        /// Renders a <see cref="PageLayout"/> to the short string persisted on <c>ClassicPage.Layout</c>,
        /// matching the legacy scanner's page CSV (the <c>Wiki_</c> / <c>WebPart_</c> prefix is dropped).
        /// </summary>
        internal static string ToLayoutString(PageLayout layout)
        {
            return layout.ToString().Replace("Wiki_", "").Replace("WebPart_", "");
        }
    }
}
