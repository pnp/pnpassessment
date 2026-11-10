using PnP.Scanning.Core.ClassicPages.Collection.List;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using FluentAssertions;
using PnP.Core.Model.Security;
using PnP.Core.Model.SharePoint;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners
{
    /// <summary>
    /// Verifies ASPX readiness typing and detached editor capture independently of acquisition.
    /// </summary>
    public class ClassicPageRulesTests
    {
        // SharePoint internal field names used by ASPX readiness metadata.
        private const string FileTypeField = "File_x0020_Type";
        private const string HtmlFileTypeField = "HTML_x0020_File_x0020_Type";
        private const string WikiField = "WikiField";
        private const string BSNField = "BSN";
        private const string ModifiedByField = "Editor";

        /// <summary>Verifies get page type retired file marker does not create delve page.</summary>
        [Fact]
        public void GetPageType_Retired_file_marker_does_not_create_Delve_page()
        {
            // A retired file-type marker must not reintroduce a Delve Page projection.
            Dictionary<string, object> fields = new Dictionary<string, object> { { FileTypeField, "pointpub" }, };

            ClassicPageRules.GetPageType(fields).Should().Be(ClassicPageRules.ASPXPage);
        }

        /// <summary>Verifies get page type retired file marker casing does not admit content.</summary>
        [Fact]
        public void GetPageType_Retired_file_marker_casing_does_not_admit_content()
        {
            Dictionary<string, object> fields = new Dictionary<string, object> { { FileTypeField, "PointPub" }, };

            ClassicPageRules.GetPageType(fields).Should().Be(ClassicPageRules.ASPXPage);
        }

        /// <summary>Verifies get page type non delve item does not return delve blog.</summary>
        [Fact]
        public void GetPageType_NonDelveItem_DoesNotReturnDelveBlog()
        {
            // Control 1: a classic web part page is still typed as a web part page.
            var webPartPage = new Dictionary<string, object>
            {
                { HtmlFileTypeField, "SharePoint.WebPartPage.Document" }, { FileTypeField, "aspx" },
            };
            ClassicPageRules.GetPageType(webPartPage).Should().Be(ClassicPageRules.WebPartPage);

            // Control 2: a plain aspx page (no pointpub marker) is never classified as Delve.
            Dictionary<string, object> aspxPage =
                new Dictionary<string, object> { { FileTypeField, "aspx" }, { BSNField, "1" }, };
            ClassicPageRules.GetPageType(aspxPage).Should().NotBe(ClassicPageRules.DelveBlogPage);

            // Control 3: a wiki page (WikiField present) wins before the Delve check.
            var wikiPage = new Dictionary<string, object>
            {
                { WikiField, "<div></div>" }, { FileTypeField, "pointpub" },
            };
            ClassicPageRules.GetPageType(wikiPage).Should().Be(ClassicPageRules.WikiPage);
        }

        /// <summary>Verifies modified by editor field returns email.</summary>
        [Fact]
        public void ModifiedBy_EditorField_ReturnsEmail()
        {
            // Parity with legacy LastModifiedBy: the account email wins when present.
            var fields = new Dictionary<string, object>
            {
                { ModifiedByField, new FakeUserValue(email: "jane.doe@contoso.com", lookupValue: "Jane Doe") },
            };

            FilePageProjection.ModifiedBy(ListItemContentReader.Fields(fields), skipUserInformation: false)
                .Should()
                .Be("jane.doe@contoso.com");
        }

        /// <summary>Verifies modified by editor field falls back to lookup value when email empty.</summary>
        [Fact]
        public void ModifiedBy_EditorField_FallsBackToLookupValue_WhenEmailEmpty()
        {
            var fields = new Dictionary<string, object>
            {
                { ModifiedByField, new FakeUserValue(email: "", lookupValue: "John Smith") },
            };

            FilePageProjection.ModifiedBy(ListItemContentReader.Fields(fields), skipUserInformation: false)
                .Should()
                .Be("John Smith");
        }

        /// <summary>Verifies modified by skip user information is null.</summary>
        [Fact]
        public void ModifiedBy_SkipUserInformation_IsNull()
        {
            var fields = new Dictionary<string, object>
            {
                { ModifiedByField, new FakeUserValue(email: "jane.doe@contoso.com", lookupValue: "Jane Doe") },
            };

            FilePageProjection.ModifiedBy(ListItemContentReader.Fields(fields), skipUserInformation: true)
                .Should()
                .BeNull();
        }

        /// <summary>Verifies modified by no editor field is null.</summary>
        [Fact]
        public void ModifiedBy_NoEditorField_IsNull()
        {
            FilePageProjection.ModifiedBy(new(), skipUserInformation: false).Should().BeNull();
        }

        /// <summary>
        /// Minimal stand-in for the user value a list-item user field exposes. Only the members the
        /// ModifiedBy parser reads (LookupValue, Title) are meaningful; the rest satisfy the interface.
        /// </summary>
        private sealed class FakeUserValue : IFieldUserValue
        {
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