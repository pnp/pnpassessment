using System.Collections.Generic;
using FluentAssertions;
using Microsoft.SharePoint.Client;
using PnP.Scanning.Core.Pipeline.Analysis;
using PnP.Scanning.Core.Pipeline.Collection;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline.Analysis
{
    /// <summary>Tests web part type inference and layout names from captured field values.</summary>
    public class PageWebPartAnalysisTests
    {
        [Theory]
        // ScriptEditor is detected purely by the presence of a "Content" property.
        [InlineData("ScriptEditorWebPart", "Content")]
        // The full distinguishing signature for an XSLT list view.
        [InlineData("XsltListViewWebPart", "ListUrl", "ListId", "Xsl", "JSLink", "ShowTimelineIfAvailable")]
        // A members web part.
        [InlineData("MembersWebPart", "NumberLimit", "DisplayType", "MembershipGroupId", "Toolbar")]
        // A media web part.
        [InlineData("MediaWebPart", "AutoPlay", "MediaSource", "Loop", "IsPreviewImageSourceOverridenForVideoSet", "PreviewImageSource")]
        public void GetTypeFromProperties_KnownSignature_ResolvesType(string expectedTypeFragment, params string[] propertyNames)
        {
            var properties = new Dictionary<string, object>();
            foreach (var name in propertyNames)
            {
                properties.Add(name, "value");
            }

            var type = PageWebPartAnalysis.GetTypeFromProperties(properties);

            type.Should().Contain(expectedTypeFragment);
        }

        [Fact]
        public void GetTypeFromProperties_UnknownSignature_ReturnsUnsupported()
        {
            var properties = new Dictionary<string, object>
            {
                { "SomeRandomProperty", "value" },
            };

            var type = PageWebPartAnalysis.GetTypeFromProperties(properties);

            type.Should().Be("Unsupported Web Part Type");
        }

        [Fact]
        public void GetTypeFromXml_V3WebPart_ReturnsTypeName()
        {
            // The exported XML for a modern (v3) web part carries the fully-qualified type in
            // metaData/type/@name.
            const string v3Xml = @"<webParts xmlns=""http://schemas.microsoft.com/WebPart/v3"">
              <webPart>
                <metaData>
                  <type name=""Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c"" />
                </metaData>
              </webPart>
            </webParts>";

            var type = PageWebPartAnalysis.GetTypeFromXml(v3Xml);

            type.Should().Be("Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c");
        }

        [Fact]
        public void GetTypeFromXml_V2WebPart_ReturnsTypeNameAndAssembly()
        {
            // A legacy (v2) export splits the type into a TypeName + Assembly pair which is recombined
            // into the "TypeName, Assembly" form.
            const string v2Xml = @"<WebPart xmlns=""http://schemas.microsoft.com/WebPart/v2"">
              <Assembly>Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c</Assembly>
              <TypeName>Microsoft.SharePoint.WebPartPages.ContentEditorWebPart</TypeName>
            </WebPart>";

            var type = PageWebPartAnalysis.GetTypeFromXml(v2Xml);

            type.Should().Be("Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void GetTypeFromXml_EmptyXml_ReturnsUnknown(string xml)
        {
            PageWebPartAnalysis.GetTypeFromXml(xml).Should().Be("Unknown");
        }

        // Publishing layout names come from the captured URL field description.

        [Fact]
        public void GetPublishingPageLayoutName_UrlFieldDescription_ReturnsLayoutName()
        {
            // The PublishingPageLayout field is a URL field whose Description holds the friendly layout
            // name; that name (e.g. "ArticleLeft") is what the legacy scanner records as the page layout.
            var fieldValues = new Dictionary<string, object>
            {
                { "PublishingPageLayout", new FieldUrlValue { Url = "/_catalogs/masterpage/ArticleLeft.aspx", Description = "ArticleLeft" } },
            };

            PageLayoutDetector.GetPublishingPageLayoutName(ClassicPageOnlineSource.Fields(fieldValues)).Should().Be("ArticleLeft");
        }

        [Fact]
        public void GetPublishingPageLayoutName_FieldMissing_ReturnsEmpty()
        {
            var fieldValues = new Dictionary<string, object>
            {
                { "SomeOtherField", "value" },
            };

            PageLayoutDetector.GetPublishingPageLayoutName(ClassicPageOnlineSource.Fields(fieldValues)).Should().BeEmpty();
        }

        [Fact]
        public void GetPublishingPageLayoutName_NullFieldValue_ReturnsEmpty()
        {
            var fieldValues = new Dictionary<string, object>
            {
                { "PublishingPageLayout", null },
            };

            PageLayoutDetector.GetPublishingPageLayoutName(ClassicPageOnlineSource.Fields(fieldValues)).Should().BeEmpty();
        }

        [Fact]
        public void GetPublishingPageLayoutName_EmptyDescription_ReturnsEmpty()
        {
            // A URL field that carries a Url but no Description yields no usable layout name.
            var fieldValues = new Dictionary<string, object>
            {
                { "PublishingPageLayout", new FieldUrlValue { Url = "/_catalogs/masterpage/ArticleLeft.aspx", Description = "" } },
            };

            PageLayoutDetector.GetPublishingPageLayoutName(ClassicPageOnlineSource.Fields(fieldValues)).Should().BeEmpty();
        }

        [Fact]
        public void GetPublishingPageLayoutName_NullFieldValues_ReturnsEmpty()
        {
            PageLayoutDetector.GetPublishingPageLayoutName(null).Should().BeEmpty();
        }
    }
}
