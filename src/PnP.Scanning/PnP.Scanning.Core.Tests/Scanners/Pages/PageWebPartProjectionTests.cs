using PnP.Scanning.Core.ClassicPages.Collection.List;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using FluentAssertions;
using Microsoft.SharePoint.Client;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages
{
    /// <summary>
    /// The detached sub-logic that publishing-page and web-part-page projection relies on:
    /// determining a web part's type from its exported XML or, when it cannot be exported, from its
    /// property signature, together with captured publishing layout declarations.
    /// </summary>
    public class PageWebPartProjectionTests
    {
        /// <summary>Verifies get type from properties known signature resolves type.</summary>
        [Theory]
        // ScriptEditor is detected purely by the presence of a "Content" property.
        [InlineData("ScriptEditorWebPart", "Content")]
        // The full distinguishing signature for an XSLT list view.
        [InlineData("XsltListViewWebPart", "ListUrl", "ListId", "Xsl", "JSLink", "ShowTimelineIfAvailable")]
        // A members web part.
        [InlineData("MembersWebPart", "NumberLimit", "DisplayType", "MembershipGroupId", "Toolbar")]
        // A media web part.
        [InlineData(
            "MediaWebPart",
            "AutoPlay",
            "MediaSource",
            "Loop",
            "IsPreviewImageSourceOverridenForVideoSet",
            "PreviewImageSource")]
        public void GetTypeFromProperties_KnownSignature_ResolvesType(
            string expectedTypeFragment,
            params string[] propertyNames)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>();
            foreach (string name in propertyNames)
            {
                properties.Add(name, "value");
            }

            string type = PageWebPartProjection.GetTypeFromProperties(properties);

            type.Should().Contain(expectedTypeFragment);
        }

        /// <summary>Verifies get type from properties unknown signature returns unsupported.</summary>
        [Fact]
        public void GetTypeFromProperties_UnknownSignature_ReturnsUnsupported()
        {
            Dictionary<string, object> properties =
                new Dictionary<string, object> { { "SomeRandomProperty", "value" }, };

            string type = PageWebPartProjection.GetTypeFromProperties(properties);

            type.Should().Be("Unsupported Web Part Type");
        }

        /// <summary>Verifies get type from xml v3 web part returns type name.</summary>
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

            string type = PageWebPartProjection.GetTypeFromXml(v3Xml);

            type.Should()
                .Be(
                    "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c");
        }

        /// <summary>Verifies get type from xml v2 web part returns type name and assembly.</summary>
        [Fact]
        public void GetTypeFromXml_V2WebPart_ReturnsTypeNameAndAssembly()
        {
            // A legacy (v2) export splits the type into a TypeName + Assembly pair which is recombined
            // into the "TypeName, Assembly" form.
            const string v2Xml = @"<WebPart xmlns=""http://schemas.microsoft.com/WebPart/v2"">
              <Assembly>Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c</Assembly>
              <TypeName>Microsoft.SharePoint.WebPartPages.ContentEditorWebPart</TypeName>
            </WebPart>";

            string type = PageWebPartProjection.GetTypeFromXml(v2Xml);

            type.Should()
                .Be(
                    "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c");
        }

        /// <summary>Verifies get type from xml empty xml returns unknown.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void GetTypeFromXml_EmptyXml_ReturnsUnknown(string xml)
        {
            PageWebPartProjection.GetTypeFromXml(xml).Should().Be("Unknown");
        }

        /// <summary>Verifies get publishing page layout name url field description returns layout name.</summary>

        // --- T7: publishing page layout name (parity with the legacy PublishingAnalyzer) -----------------
        [Fact]
        public void GetPublishingPageLayoutName_UrlFieldDescription_ReturnsLayoutName()
        {
            // The PublishingPageLayout field is a URL field whose Description holds the friendly layout
            // name; that name (e.g. "ArticleLeft") is what the legacy scanner records as the page layout.
            Dictionary<string, object> fieldValues = new Dictionary<string, object>
            {
                {
                    "PublishingPageLayout",
                    new FieldUrlValue { Url = "/_catalogs/masterpage/ArticleLeft.aspx", Description = "ArticleLeft" }
                },
            };

            SnapshotPageLayout.GetPublishingPageLayoutName(ListItemContentReader.Fields(fieldValues))
                .Should()
                .Be("ArticleLeft");
            SnapshotPageLayout.GetPublishingPageLayoutUrl(ListItemContentReader.Fields(fieldValues))
                .Should()
                .Be("/_catalogs/masterpage/ArticleLeft.aspx");
        }

        /// <summary>Verifies get publishing page layout name field missing returns empty.</summary>
        [Fact]
        public void GetPublishingPageLayoutName_FieldMissing_ReturnsEmpty()
        {
            Dictionary<string, object> fieldValues = new Dictionary<string, object> { { "SomeOtherField", "value" }, };

            SnapshotPageLayout.GetPublishingPageLayoutName(ListItemContentReader.Fields(fieldValues))
                .Should()
                .BeEmpty();
            SnapshotPageLayout.GetPublishingPageLayoutUrl(ListItemContentReader.Fields(fieldValues)).Should().BeNull();
        }

        /// <summary>Verifies get publishing page layout name null field value returns empty.</summary>
        [Fact]
        public void GetPublishingPageLayoutName_NullFieldValue_ReturnsEmpty()
        {
            Dictionary<string, object> fieldValues =
                new Dictionary<string, object> { { "PublishingPageLayout", null }, };

            SnapshotPageLayout.GetPublishingPageLayoutName(ListItemContentReader.Fields(fieldValues))
                .Should()
                .BeEmpty();
            SnapshotPageLayout.GetPublishingPageLayoutUrl(ListItemContentReader.Fields(fieldValues)).Should().BeNull();
        }

        /// <summary>Verifies get publishing page layout name empty description returns empty.</summary>
        [Fact]
        public void GetPublishingPageLayoutName_EmptyDescription_ReturnsEmpty()
        {
            // A URL field that carries a Url but no Description yields no usable layout name.
            Dictionary<string, object> fieldValues = new Dictionary<string, object>
            {
                {
                    "PublishingPageLayout",
                    new FieldUrlValue { Url = "/_catalogs/masterpage/ArticleLeft.aspx", Description = "" }
                },
            };

            SnapshotPageLayout.GetPublishingPageLayoutName(ListItemContentReader.Fields(fieldValues))
                .Should()
                .BeEmpty();
            SnapshotPageLayout.GetPublishingPageLayoutUrl(ListItemContentReader.Fields(fieldValues))
                .Should()
                .Be("/_catalogs/masterpage/ArticleLeft.aspx");
        }

        /// <summary>Verifies get publishing page layout name null field values returns empty.</summary>
        [Fact]
        public void GetPublishingPageLayoutName_NullFieldValues_ReturnsEmpty()
        {
            SnapshotPageLayout.GetPublishingPageLayoutName(new()).Should().BeEmpty();
            SnapshotPageLayout.GetPublishingPageLayoutUrl(new()).Should().BeNull();
        }
    }
}