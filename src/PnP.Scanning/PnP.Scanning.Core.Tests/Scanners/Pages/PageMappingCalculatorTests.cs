using System.Collections.Generic;
using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Scanners.WebPartMapping;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages
{
    /// <summary>
    /// T6 — the pure mapping-percentage computation over a page's extracted web part inventory. No
    /// CSOM, no live SharePoint: it feeds <see cref="ClassicPageWebPart"/> rows (built like the ones
    /// <c>PageWebPartExtractor</c> produces) through <see cref="PageMappingCalculator"/> backed by the
    /// real embedded <c>webpartmapping.xml</c>, and asserts the page rollups + per-part verdicts.
    /// </summary>
    public class PageMappingCalculatorTests
    {
        // A web part with a known modern mapping (mappable) and one without (unmappable), straight from
        // the legacy WebParts constants the mapping file keys on.
        private const string ContentEditorType = "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string XsltListViewType = "Microsoft.SharePoint.WebPartPages.XsltListViewWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string ImageType = "Microsoft.SharePoint.WebPartPages.ImageWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
        private const string CustomTypeA = "Contoso.Custom.WebParts.WidgetA, Contoso.Custom, Version=1.0.0.0, Culture=neutral, PublicKeyToken=0000000000000000";
        private const string CustomTypeB = "Contoso.Custom.WebParts.WidgetB, Contoso.Custom, Version=1.0.0.0, Culture=neutral, PublicKeyToken=0000000000000000";

        private readonly WebPartMappingManager mappingManager = new();

        private static ClassicPageWebPart WebPart(string fullType, int row = 1, int column = 1, int order = 0)
        {
            return new ClassicPageWebPart
            {
                WebPartType = fullType,
                WebPartTypeShort = WebPartEntity.GetTypeShort(fullType),
                Row = row,
                Column = column,
                Order = order,
            };
        }

        [Fact]
        public void MappingPercent_3of4Mappable_Returns75()
        {
            var page = new ClassicPage();
            var webParts = new List<ClassicPageWebPart>
            {
                WebPart(ContentEditorType, row: 1, column: 1, order: 0),  // mappable
                WebPart(XsltListViewType, row: 1, column: 1, order: 1),   // mappable
                WebPart(ImageType, row: 1, column: 2, order: 0),          // mappable
                WebPart(CustomTypeA, row: 2, column: 1, order: 0),        // unmapped
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

        [Fact]
        public void MappingPercent_NoWebParts_Returns100()
        {
            // Locked convention: an empty page has nothing blocking modernization → 100%, no unmapped list.
            var page = new ClassicPage();

            PageMappingCalculator.ApplyMapping(page, new List<ClassicPageWebPart>(), mappingManager);

            page.WebPartCount.Should().Be(0);
            page.MappingPercentage.Should().Be(100);
            page.UnmappedWebParts.Should().BeEmpty();
        }

        [Fact]
        public void MappingPercent_NullWebParts_Returns100()
        {
            // A null inventory is treated the same as an empty one (defensive — extractor returns a list).
            var page = new ClassicPage();

            PageMappingCalculator.ApplyMapping(page, null, mappingManager);

            page.WebPartCount.Should().Be(0);
            page.MappingPercentage.Should().Be(100);
            page.UnmappedWebParts.Should().BeEmpty();
        }

        [Fact]
        public void MappingPercent_AllUnmappable_Returns0()
        {
            var page = new ClassicPage();
            var webParts = new List<ClassicPageWebPart>
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

        [Fact]
        public void MappingPercent_DuplicateUnmappedType_ListedOnce()
        {
            // The unmapped list is de-duplicated on the short type, matching the legacy scanner.
            var page = new ClassicPage();
            var webParts = new List<ClassicPageWebPart>
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

        [Fact]
        public void MappingPercent_UnmappedList_InRowColumnOrder()
        {
            // Out-of-order rows must surface in page-layout order (row, then column, then order).
            var page = new ClassicPage();
            var webParts = new List<ClassicPageWebPart>
            {
                WebPart(CustomTypeB, row: 2, column: 1, order: 0),
                WebPart(CustomTypeA, row: 1, column: 1, order: 0),
            };

            PageMappingCalculator.ApplyMapping(page, webParts, mappingManager);

            page.UnmappedWebParts.Should().Be("Contoso.Custom.WebParts.WidgetA,Contoso.Custom.WebParts.WidgetB");
        }
    }
}
