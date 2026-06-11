using FluentAssertions;
using PnP.Core.Model.Security;
using PnP.Core.Model.SharePoint;
using PnP.Scanning.Core.Scanners;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners
{
    /// <summary>
    /// T3 — quick-win metadata fixes: Delve blog page typing and the page "Modified By" capture.
    /// Both helpers are pure functions over the raw list-item field values (no CSOM), so they are
    /// exercised here directly with fake field dictionaries.
    /// </summary>
    public class PageScanComponentTests
    {
        // SharePoint internal field names (mirrors the private constants in PageScanComponent).
        private const string FileTypeField = "File_x0020_Type";
        private const string HtmlFileTypeField = "HTML_x0020_File_x0020_Type";
        private const string WikiField = "WikiField";
        private const string BSNField = "BSN";
        private const string ModifiedByField = "Editor";

        [Fact]
        public void GetPageType_DelveBlogItem_ReturnsDelveBlog()
        {
            // A Delve blog page surfaces with the "pointpub" file type and no wiki/web part markers.
            var fields = new Dictionary<string, object>
            {
                { FileTypeField, "pointpub" },
            };

            PageScanComponent.GetPageType(fields).Should().Be(PageScanComponent.DelveBlogPage);
        }

        [Fact]
        public void GetPageType_DelveBlogFileType_IsCaseInsensitive()
        {
            var fields = new Dictionary<string, object>
            {
                { FileTypeField, "PointPub" },
            };

            PageScanComponent.GetPageType(fields).Should().Be(PageScanComponent.DelveBlogPage);
        }

        [Fact]
        public void GetPageType_NonDelveItem_DoesNotReturnDelveBlog()
        {
            // Control 1: a classic web part page is still typed as a web part page.
            var webPartPage = new Dictionary<string, object>
            {
                { HtmlFileTypeField, "SharePoint.WebPartPage.Document" },
                { FileTypeField, "aspx" },
            };
            PageScanComponent.GetPageType(webPartPage).Should().Be(PageScanComponent.WebPartPage);

            // Control 2: a plain aspx page (no pointpub marker) is never classified as Delve.
            var aspxPage = new Dictionary<string, object>
            {
                { FileTypeField, "aspx" },
                { BSNField, "1" },
            };
            PageScanComponent.GetPageType(aspxPage).Should().NotBe(PageScanComponent.DelveBlogPage);

            // Control 3: a wiki page (WikiField present) wins before the Delve check.
            var wikiPage = new Dictionary<string, object>
            {
                { WikiField, "<div></div>" },
                { FileTypeField, "pointpub" },
            };
            PageScanComponent.GetPageType(wikiPage).Should().Be(PageScanComponent.WikiPage);
        }

        [Fact]
        public void ModifiedBy_EditorField_ReturnsEmail()
        {
            // Parity with legacy LastModifiedBy: the account email wins when present.
            var fields = new Dictionary<string, object>
            {
                { ModifiedByField, new FakeUserValue(email: "jane.doe@contoso.com", lookupValue: "Jane Doe") },
            };

            PageScanComponent.GetModifiedBy(fields, skipUserInformation: false).Should().Be("jane.doe@contoso.com");
        }

        [Fact]
        public void ModifiedBy_EditorField_FallsBackToLookupValue_WhenEmailEmpty()
        {
            var fields = new Dictionary<string, object>
            {
                { ModifiedByField, new FakeUserValue(email: "", lookupValue: "John Smith") },
            };

            PageScanComponent.GetModifiedBy(fields, skipUserInformation: false).Should().Be("John Smith");
        }

        [Fact]
        public void ModifiedBy_SkipUserInformation_IsNull()
        {
            var fields = new Dictionary<string, object>
            {
                { ModifiedByField, new FakeUserValue(email: "jane.doe@contoso.com", lookupValue: "Jane Doe") },
            };

            PageScanComponent.GetModifiedBy(fields, skipUserInformation: true).Should().BeNull();
        }

        [Fact]
        public void ModifiedBy_NoEditorField_IsNull()
        {
            PageScanComponent.GetModifiedBy(new Dictionary<string, object>(), skipUserInformation: false).Should().BeNull();
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
