using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using FluentAssertions;
using PnP.Scanning.Core.Scanners.WebPartMapping;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages
{
    /// <summary>
    /// T6 — the pure mapping-percentage computation over a page's extracted web part inventory. No
    /// CSOM, no live SharePoint: it feeds <see cref="ClassicPageWebPartRow"/> rows (built like the ones
    /// <c>PageWebPartExtractor</c> produces) through <see cref="PageMappingCalculator"/> backed by the
    /// real embedded <c>webpartmapping.xml</c>, and asserts the page rollups + per-part verdicts.
    /// </summary>
    public class PageMappingCalculatorTests
    {
        // A web part with a known modern mapping (mappable) and one without (unmappable), straight from
        // the legacy WebParts constants the mapping file keys on.
        private const string ContentEditorType =
            "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

        private const string XsltListViewType =
            "Microsoft.SharePoint.WebPartPages.XsltListViewWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

        private const string ImageType =
            "Microsoft.SharePoint.WebPartPages.ImageWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";

        private const string CustomTypeA =
            "Contoso.Custom.WebParts.WidgetA, Contoso.Custom, Version=1.0.0.0, Culture=neutral, PublicKeyToken=0000000000000000";

        private const string CustomTypeB =
            "Contoso.Custom.WebParts.WidgetB, Contoso.Custom, Version=1.0.0.0, Culture=neutral, PublicKeyToken=0000000000000000";

        private readonly WebPartMappingManager mappingManager = new();

        /// <summary>Builds a detached web-part row with its type and page-layout coordinates.</summary>
        private static ClassicPageWebPartRow WebPart(string fullType, int row = 1, int column = 1, int order = 0)
        {
            return new ClassicPageWebPartRow
            {
                WebPartType = fullType,
                WebPartTypeShort = WebPartEntity.GetTypeShort(fullType),
                Row = row,
                Column = column,
                Order = order,
            };
        }

        /// <summary>Verifies mapping percent 3of4 mappable returns75.</summary>
        [Fact]
        public void MappingPercent_3of4Mappable_Returns75()
        {
            ClassicPageRow page = new ClassicPageRow();
            List<ClassicPageWebPartRow> webParts = new List<ClassicPageWebPartRow>
            {
                WebPart(ContentEditorType, row: 1, column: 1, order: 0), // mappable
                WebPart(XsltListViewType, row: 1, column: 1, order: 1), // mappable
                WebPart(ImageType, row: 1, column: 2, order: 0), // mappable
                WebPart(CustomTypeA, row: 2, column: 1, order: 0), // unmapped
            };

            PageMappingCalculator.ApplyMapping(page, webParts, mappingManager);

            page.WebPartCount.Should().Be(4);
            page.MappingPercentage.Should().Be(75);
            page.UnmappedWebParts.Should().Be("Contoso.Custom.WebParts.WidgetA");

            // Per-part verdicts are stamped on the rows so they can be persisted.
            webParts[0].IsMappable.Should().BeTrue();
            webParts[1].IsMappable.Should().BeTrue();
            webParts[2].IsMappable.Should().BeTrue();
            webParts[3].IsMappable.Should().BeFalse();
        }

        /// <summary>Verifies mapping percent no web parts returns100.</summary>
        [Fact]
        public void MappingPercent_NoWebParts_Returns100()
        {
            // Locked convention: an empty page has nothing blocking modernization → 100%, no unmapped list.
            ClassicPageRow page = new ClassicPageRow();

            PageMappingCalculator.ApplyMapping(page, new List<ClassicPageWebPartRow>(), mappingManager);

            page.WebPartCount.Should().Be(0);
            page.MappingPercentage.Should().Be(100);
            page.UnmappedWebParts.Should().BeEmpty();
        }

        /// <summary>Verifies mapping percent null web parts returns100.</summary>
        [Fact]
        public void MappingPercent_NullWebParts_Returns100()
        {
            // A null inventory is treated the same as an empty one (defensive — extractor returns a list).
            ClassicPageRow page = new ClassicPageRow();

            PageMappingCalculator.ApplyMapping(page, null, mappingManager);

            page.WebPartCount.Should().Be(0);
            page.MappingPercentage.Should().Be(100);
            page.UnmappedWebParts.Should().BeEmpty();
        }

        /// <summary>Verifies mapping percent all unmappable returns0.</summary>
        [Fact]
        public void MappingPercent_AllUnmappable_Returns0()
        {
            ClassicPageRow page = new ClassicPageRow();
            List<ClassicPageWebPartRow> webParts = new List<ClassicPageWebPartRow>
            {
                WebPart(CustomTypeA, row: 1, column: 1, order: 0),
                WebPart(CustomTypeB, row: 1, column: 1, order: 1),
            };

            PageMappingCalculator.ApplyMapping(page, webParts, mappingManager);

            page.WebPartCount.Should().Be(2);
            page.MappingPercentage.Should().Be(0);
            page.UnmappedWebParts.Should().Be("Contoso.Custom.WebParts.WidgetA,Contoso.Custom.WebParts.WidgetB");
            webParts.Should().OnlyContain(wp => wp.IsMappable == false);
        }

        /// <summary>Verifies mapping percent duplicate unmapped type listed once.</summary>
        [Fact]
        public void MappingPercent_DuplicateUnmappedType_ListedOnce()
        {
            // The unmapped list is de-duplicated on the short type, matching the legacy scanner.
            ClassicPageRow page = new ClassicPageRow();
            List<ClassicPageWebPartRow> webParts = new List<ClassicPageWebPartRow>
            {
                WebPart(CustomTypeA, row: 1, column: 1, order: 0),
                WebPart(CustomTypeA, row: 1, column: 1, order: 1),
                WebPart(ContentEditorType, row: 2, column: 1, order: 0),
            };

            PageMappingCalculator.ApplyMapping(page, webParts, mappingManager);

            page.WebPartCount.Should().Be(3);
            // 1 of 3 mappable → 33.33% rounds to 33.
            page.MappingPercentage.Should().Be(33);
            page.UnmappedWebParts.Should().Be("Contoso.Custom.WebParts.WidgetA");
        }

        /// <summary>Verifies mapping percent unmapped list in row column order.</summary>
        [Fact]
        public void MappingPercent_UnmappedList_InRowColumnOrder()
        {
            // Out-of-order rows must surface in page-layout order (row, then column, then order).
            ClassicPageRow page = new ClassicPageRow();
            List<ClassicPageWebPartRow> webParts = new List<ClassicPageWebPartRow>
            {
                WebPart(CustomTypeB, row: 2, column: 1, order: 0),
                WebPart(CustomTypeA, row: 1, column: 1, order: 0),
            };

            PageMappingCalculator.ApplyMapping(page, webParts, mappingManager);

            page.UnmappedWebParts.Should().Be("Contoso.Custom.WebParts.WidgetA,Contoso.Custom.WebParts.WidgetB");
        }
    }
}