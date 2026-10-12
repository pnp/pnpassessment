using FluentAssertions;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Analysis.Page.WebPartMapping;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline.Analysis.Page
{
    /// <summary>
    /// Verifies wiki, web part and publishing layout recognition from detached page fields and markup.
    /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/Pages/PageLayoutDetectorTests.cs</c> to keep saved-markup layout regressions with offline page analysis.</para>
    /// </summary>
    public class PageLayoutDetectorTests
    {
        // --- Wiki layout: from the layoutsdata span (the primary path) -----------------------------------

        /// <summary>
        /// Verifies recognition of supported wiki layout families from saved layouts data.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/Pages/PageLayoutDetectorTests.cs</c> to keep saved-markup layout regressions with offline page analysis.</para>
        /// </summary>
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

        /// <summary>
        /// Verifies the one-column layout convention for an empty wiki page.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/Pages/PageLayoutDetectorTests.cs</c> to keep saved-markup layout regressions with offline page analysis.</para>
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Layout_WikiEmpty_IsOneColumn(string html)
        {
            // Parity with the legacy scanner: an empty wiki page is a one-column layout.
            PageLayoutDetector.DetectWikiLayout(html).Should().Be(PageLayout.Wiki_OneColumn);
        }

        /// <summary>
        /// Verifies a custom layout classification when wiki markup has no layouts data.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/Pages/PageLayoutDetectorTests.cs</c> to keep saved-markup layout regressions with offline page analysis.</para>
        /// </summary>
        [Fact]
        public void Layout_WikiNoLayoutsData_IsCustom()
        {
            // No recognizable layout markup at all → custom layout.
            PageLayoutDetector.DetectWikiLayout("<div><p>free standing wiki content</p></div>")
                .Should().Be(PageLayout.Wiki_Custom);
        }

        /// <summary>
        /// Verifies column-count fallback for unrecognized wiki layouts data.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/Pages/PageLayoutDetectorTests.cs</c> to keep saved-markup layout regressions with offline page analysis.</para>
        /// </summary>
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

        /// <summary>
        /// Verifies recognition of known web part layout templates from their setup paths.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/Pages/PageLayoutDetectorTests.cs</c> to keep saved-markup layout regressions with offline page analysis.</para>
        /// </summary>
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

        /// <summary>
        /// Verifies custom layout classification for an unknown web part setup path.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/Pages/PageLayoutDetectorTests.cs</c> to keep saved-markup layout regressions with offline page analysis.</para>
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(@"1033\STS\doctemp\smartpgs\unknown.aspx")]
        public void Layout_WebPartUnknownSetupPath_IsCustom(string setupPath)
        {
            PageLayoutDetector.DetectWebPartPageLayout(setupPath).Should().Be(PageLayout.WebPart_Custom);
        }

        // Publishing-page layout is NOT derived here — it is the actual page layout name read from the
        // PublishingPageLayout field; see PageWebPartAnalysisTests.GetPublishingPageLayoutName_*.

        // --- Layout string rendering (what lands on ClassicPage.Layout for wiki / web part pages) --------

        /// <summary>
        /// Verifies the existing report layout names after removing wiki and web part enum prefixes.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/Pages/PageLayoutDetectorTests.cs</c> to keep saved-markup layout regressions with offline page analysis.</para>
        /// </summary>
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
        /// <summary>
        /// Provides recorded wiki layout markup and the expected layout classification for parameterized tests.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/Pages/PageLayoutDetectorTests.cs</c> to keep saved-markup layout regressions with offline page analysis.</para>
        /// </summary>
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
