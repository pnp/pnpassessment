using FluentAssertions;
using PnP.Core.Model.Security;
using PnP.Core.Model.SharePoint;
using Xunit;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Collection.Module;

namespace PnP.Scanning.Core.Tests.Pipeline.Analysis.Page
{
    /// <summary>
    /// Verifies saved-field page classification and editor projection without retaining SDK objects in analysis.
    /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/PageScanComponentTests.cs</c> to test pure page classification and detached editor projection after splitting the scanner.</para>
    /// </summary>
    public class ClassicPageRulesTests
    {
        // SharePoint internal field names (used by the collection query and analysis rules).
        private const string FileTypeField = "File_x0020_Type";
        private const string HtmlFileTypeField = "HTML_x0020_File_x0020_Type";
        private const string WikiField = "WikiField";
        private const string BSNField = "BSN";
        private const string ModifiedByField = "Editor";

        /// <summary>
        /// Verifies classification of a saved point-publishing item as a Delve blog page.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/PageScanComponentTests.cs</c> to test pure page classification and detached editor projection after splitting the scanner.</para>
        /// </summary>
        [Fact]
        public void GetPageType_DelveBlogItem_ReturnsDelveBlog()
        {
            // A Delve blog page surfaces with the "pointpub" file type and no wiki/web part markers.
            var fields = new Dictionary<string, object>
            {
                { FileTypeField, "pointpub" },
            };

            ClassicPageRules.GetPageType(fields).Should().Be(ClassicPageRules.DelveBlogPage);
        }

        /// <summary>
        /// Verifies case-insensitive recognition of the Delve blog file type.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/PageScanComponentTests.cs</c> to test pure page classification and detached editor projection after splitting the scanner.</para>
        /// </summary>
        [Fact]
        public void GetPageType_DelveBlogFileType_IsCaseInsensitive()
        {
            var fields = new Dictionary<string, object>
            {
                { FileTypeField, "PointPub" },
            };

            ClassicPageRules.GetPageType(fields).Should().Be(ClassicPageRules.DelveBlogPage);
        }

        /// <summary>
        /// Verifies that other captured file types are not classified as Delve blog pages.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/PageScanComponentTests.cs</c> to test pure page classification and detached editor projection after splitting the scanner.</para>
        /// </summary>
        [Fact]
        public void GetPageType_NonDelveItem_DoesNotReturnDelveBlog()
        {
            // Control 1: a classic web part page is still typed as a web part page.
            var webPartPage = new Dictionary<string, object>
            {
                { HtmlFileTypeField, "SharePoint.WebPartPage.Document" },
                { FileTypeField, "aspx" },
            };
            ClassicPageRules.GetPageType(webPartPage).Should().Be(ClassicPageRules.WebPartPage);

            // Control 2: a plain aspx page (no pointpub marker) is never classified as Delve.
            var aspxPage = new Dictionary<string, object>
            {
                { FileTypeField, "aspx" },
                { BSNField, "1" },
            };
            ClassicPageRules.GetPageType(aspxPage).Should().NotBe(ClassicPageRules.DelveBlogPage);

            // Control 3: a wiki page (WikiField present) wins before the Delve check.
            var wikiPage = new Dictionary<string, object>
            {
                { WikiField, "<div></div>" },
                { FileTypeField, "pointpub" },
            };
            ClassicPageRules.GetPageType(wikiPage).Should().Be(ClassicPageRules.WikiPage);
        }

        /// <summary>
        /// Verifies that the saved editor email is preferred for the page author projection.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/PageScanComponentTests.cs</c> to test pure page classification and detached editor projection after splitting the scanner.</para>
        /// </summary>
        [Fact]
        public void ModifiedBy_EditorField_ReturnsEmail()
        {
            // Parity with legacy LastModifiedBy: the account email wins when present.
            var fields = new Dictionary<string, object>
            {
                { ModifiedByField, new FakeUserValue(email: "jane.doe@contoso.com", lookupValue: "Jane Doe") },
            };

            ClassicPageProjection.ModifiedBy(ClassicPageOnlineSource.Fields(fields), skipUserInformation: false).Should().Be("jane.doe@contoso.com");
        }

        /// <summary>
        /// Verifies editor display-value fallback when a captured user has no email.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/PageScanComponentTests.cs</c> to test pure page classification and detached editor projection after splitting the scanner.</para>
        /// </summary>
        [Fact]
        public void ModifiedBy_EditorField_FallsBackToLookupValue_WhenEmailEmpty()
        {
            var fields = new Dictionary<string, object>
            {
                { ModifiedByField, new FakeUserValue(email: "", lookupValue: "John Smith") },
            };

            ClassicPageProjection.ModifiedBy(ClassicPageOnlineSource.Fields(fields), skipUserInformation: false).Should().Be("John Smith");
        }

        /// <summary>
        /// Verifies that excluding user information suppresses the projected editor value.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/PageScanComponentTests.cs</c> to test pure page classification and detached editor projection after splitting the scanner.</para>
        /// </summary>
        [Fact]
        public void ModifiedBy_SkipUserInformation_IsNull()
        {
            var fields = new Dictionary<string, object>
            {
                { ModifiedByField, new FakeUserValue(email: "jane.doe@contoso.com", lookupValue: "Jane Doe") },
            };

            ClassicPageProjection.ModifiedBy(ClassicPageOnlineSource.Fields(fields), skipUserInformation: true).Should().BeNull();
        }

        /// <summary>
        /// Verifies a null editor projection when the source contains no Editor field.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/PageScanComponentTests.cs</c> to test pure page classification and detached editor projection after splitting the scanner.</para>
        /// </summary>
        [Fact]
        public void ModifiedBy_NoEditorField_IsNull()
        {
            ClassicPageProjection.ModifiedBy(ClassicPageOnlineSource.Fields(new Dictionary<string, object>()), skipUserInformation: false).Should().BeNull();
        }

        /// <summary>
        /// Supplies a modeled user value so collection field capture can be tested against SDK user metadata.
        /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/PageScanComponentTests.cs</c> to test pure page classification and detached editor projection after splitting the scanner.</para>
        /// </summary>
        private sealed class FakeUserValue : IFieldUserValue
        {
            /// <summary>
            /// Binds the email and lookup identity returned by the modeled test user.
            /// <para>Evolved from <c>PnP.Scanning.Core.Tests/Scanners/PageScanComponentTests.cs</c> to test pure page classification and detached editor projection after splitting the scanner.</para>
            /// </summary>
            public FakeUserValue(string email = null, string lookupValue = null, string title = null)
            {
                Email = email;
                LookupValue = lookupValue;
                Title = title;
            }

            public string LookupValue { get; }

            public string Title { get; }

            public IField Field => null;

            public int LookupId { get; set; }

            public bool IsSecretFieldValue => false;

            public ISharePointPrincipal Principal { get; set; }

            public string Email { get; }

            public string Sip => null;

            public string Picture => null;
        }
    }
}
