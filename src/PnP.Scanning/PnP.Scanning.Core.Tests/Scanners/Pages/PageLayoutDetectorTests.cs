using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Scanners.WebPartMapping;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages
{
    /// <summary>
    /// T7 — page layout detection. Pure (no CSOM) parsing of the wiki <c>WikiField</c> HTML, the web part
    /// page <c>vti_setuppath</c> property string, and the constant publishing-page layout, plus the
    /// rendering of a <see cref="PageLayout"/> to the short string stored on <c>ClassicPage.Layout</c>.
    /// One case per representative layout family.
    /// </summary>
    public class PageLayoutDetectorTests
    {
        // --- Wiki layout: from the layoutsdata span (the primary path) -----------------------------------

        [Theory]
        // OneColumn needs no width hint.
        [InlineData("false,false,1", null, PageLayout.Wiki_OneColumn)]
        // Two columns: the first styled td's width disambiguates equal-width vs sidebar.
        [InlineData("false,false,2", "width:49.95%;", PageLayout.Wiki_TwoColumns)]
        [InlineData("false,false,2", "width:66.6%;", PageLayout.Wiki_TwoColumnsWithSidebar)]
        [InlineData("true,false,2", null, PageLayout.Wiki_TwoColumnsWithHeader)]
        [InlineData("true,true,2", null, PageLayout.Wiki_TwoColumnsWithHeaderAndFooter)]
        [InlineData("false,false,3", null, PageLayout.Wiki_ThreeColumns)]
        [InlineData("true,false,3", null, PageLayout.Wiki_ThreeColumnsWithHeader)]
        [InlineData("true,true,3", null, PageLayout.Wiki_ThreeColumnsWithHeaderAndFooter)]
        public void Layout_WikiLayoutsDataSpan_DetectsFamily(string layoutsData, string firstTdStyle, PageLayout expected)
        {
            var html = WikiWithLayoutsData(layoutsData, firstTdStyle);

            PageLayoutDetector.DetectWikiLayout(html).Should().Be(expected);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Layout_WikiEmpty_IsOneColumn(string html)
        {
            // Parity with the legacy scanner: an empty wiki page is a one-column layout.
            PageLayoutDetector.DetectWikiLayout(html).Should().Be(PageLayout.Wiki_OneColumn);
        }

        [Fact]
        public void Layout_WikiNoLayoutsData_IsCustom()
        {
            // No recognizable layout markup at all → custom layout.
            PageLayoutDetector.DetectWikiLayout("<div><p>free standing wiki content</p></div>")
                .Should().Be(PageLayout.Wiki_Custom);
        }

        [Fact]
        public void Layout_WikiUnrecognizedSpan_FallsBackToColumnCount()
        {
            // Some pages (e.g. community-template pages) leave the layoutsdata token unsubstituted; the
            // fallback then deduces the layout from the styled-column count. "false,false,{0}" with three
            // styled columns ⇒ three-column layout.
            var html = WikiWithLayoutsData("false,false,{0}", "width:33%;", "width:33%;", "width:33%;");

            PageLayoutDetector.DetectWikiLayout(html).Should().Be(PageLayout.Wiki_ThreeColumns);
        }

        // --- Web part page layout: from the vti_setuppath property ---------------------------------------

        [Theory]
        [InlineData(@"1033\STS\doctemp\smartpgs\spstd1.aspx", PageLayout.WebPart_FullPageVertical)]
        [InlineData(@"1033\STS\doctemp\smartpgs\spstd2.aspx", PageLayout.WebPart_HeaderFooterThreeColumns)]
        [InlineData(@"1033\STS\doctemp\smartpgs\spstd3.aspx", PageLayout.WebPart_HeaderLeftColumnBody)]
        [InlineData(@"1033\STS\doctemp\smartpgs\spstd4.aspx", PageLayout.WebPart_HeaderRightColumnBody)]
        [InlineData(@"1033\STS\doctemp\smartpgs\spstd5.aspx", PageLayout.WebPart_HeaderFooter2Columns4Rows)]
        [InlineData(@"1033\STS\doctemp\smartpgs\spstd6.aspx", PageLayout.WebPart_HeaderFooter4ColumnsTopRow)]
        [InlineData(@"1033\STS\doctemp\smartpgs\spstd7.aspx", PageLayout.WebPart_LeftColumnHeaderFooterTopRow3Columns)]
        [InlineData(@"1033\STS\doctemp\smartpgs\spstd8.aspx", PageLayout.WebPart_RightColumnHeaderFooterTopRow3Columns)]
        [InlineData(@"SiteTemplates\STS\default.aspx", PageLayout.WebPart_2010_TwoColumnsLeft)]
        public void Layout_WebPartSetupPath_DetectsKnownTemplate(string setupPath, PageLayout expected)
        {
            PageLayoutDetector.DetectWebPartPageLayout(setupPath).Should().Be(expected);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(@"1033\STS\doctemp\smartpgs\unknown.aspx")]
        public void Layout_WebPartUnknownSetupPath_IsCustom(string setupPath)
        {
            PageLayoutDetector.DetectWebPartPageLayout(setupPath).Should().Be(PageLayout.WebPart_Custom);
        }

        // Publishing-page layout is NOT derived here — it is the actual page layout name read from the
        // PublishingPageLayout field; see PageWebPartExtractorTests.GetPublishingPageLayoutName_*.

        // --- Layout string rendering (what lands on ClassicPage.Layout for wiki / web part pages) --------

        [Theory]
        [InlineData(PageLayout.Wiki_OneColumn, "OneColumn")]
        [InlineData(PageLayout.Wiki_TwoColumnsWithSidebar, "TwoColumnsWithSidebar")]
        [InlineData(PageLayout.WebPart_Custom, "Custom")]
        [InlineData(PageLayout.WebPart_2010_TwoColumnsLeft, "2010_TwoColumnsLeft")]
        public void Layout_ToLayoutString_StripsWikiAndWebPartPrefixes(PageLayout layout, string expected)
        {
            PageLayoutDetector.ToLayoutString(layout).Should().Be(expected);
        }

        // Builds a minimal wiki HTML page carrying a layoutsdata span and an optional set of styled
        // columns (only the first td's style matters to the detector, but the fallback counts all of them).
        private static string WikiWithLayoutsData(string layoutsData, params string[] tdStyles)
        {
            var tds = string.Concat(tdStyles
                .Where(s => s != null)
                .Select(s => $"<td style=\"{s}\">&#160;</td>"));

            return $@"<div class=""ExternalClassABC"">
                        <span id=""layoutsdata"">{layoutsData}</span>
                        <table id=""layoutsTable""><tbody><tr>{tds}</tr></tbody></table>
                      </div>";
        }
    }
}
