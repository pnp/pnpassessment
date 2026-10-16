using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners
{
    /// <summary>
    /// Display form resolution for the item-only page route (AddDisplayFormPagesAsync). The route resolves a
    /// list item's page in two tiers: the item's content type DisplayFormUrl when set, otherwise the list's
    /// DefaultDisplayFormUrl. The two tiers are not returned in the same URL shape, and the regression this
    /// covers is that a route which only accepted already-server-relative values discarded the entire content
    /// type tier and therefore produced no rows on any tenant.
    ///
    /// The literal URL values below were read from a live tenant, not invented: every content type that
    /// carried a DisplayFormUrl returned either "Lists/Posts/Post.aspx" (the stock blog Post content type,
    /// web-relative) or "~layouts/WrkTaskIP.aspx", while list DefaultDisplayFormUrl values came back
    /// server-relative and, for 9401 of 9947 lists, named DispForm.aspx.
    /// </summary>
    public class PageScanComponentDisplayFormTests
    {
        private const string WebUrl = "/sites/team/blog";
        private const string SiteUrl = "/sites/team";

        [Fact]
        public void ToServerRelativeFormUrl_StockBlogPostContentType_ResolvesAndCountsAsContentPage()
        {
            // The regression. SharePoint stores the blog Post content type's display form web-relative, with
            // no leading slash. Discarding it collapsed resolution onto the list default (DispForm.aspx),
            // which the stock-form heuristic skips, so every list in every tenant was skipped.
            string resolved = PageScanComponent.ToServerRelativeFormUrl("Lists/Posts/Post.aspx", WebUrl, SiteUrl);

            resolved.Should().Be("/sites/team/blog/Lists/Posts/Post.aspx");
            PageScanComponent.IsContentPageForm(resolved).Should().BeTrue();
        }

        [Fact]
        public void ToServerRelativeFormUrl_WebRelativeValue_OnRootWeb_DoesNotDoubleSlash()
        {
            PageScanComponent.ToServerRelativeFormUrl("Lists/Posts/Post.aspx", "/", "/")
                .Should().Be("/Lists/Posts/Post.aspx");
        }

        [Theory]
        // Tokens ListFormWebPart.ComputeServerRelativePageUrl expands. "~sitecollectionlayouts" has to be
        // matched before "~sitecollection", which is a prefix of it.
        [InlineData("~site/Lists/Posts/Post.aspx", "/sites/team/blog/Lists/Posts/Post.aspx")]
        [InlineData("~sitecollection/Lists/Posts/Post.aspx", "/sites/team/Lists/Posts/Post.aspx")]
        [InlineData("~layouts/WrkTaskIP.aspx", "/sites/team/blog/_layouts/15/WrkTaskIP.aspx")]
        [InlineData("~sitecollectionlayouts/WrkTaskIP.aspx", "/sites/team/_layouts/15/WrkTaskIP.aspx")]
        public void ToServerRelativeFormUrl_ExpandsTokens(string formUrl, string expected)
        {
            PageScanComponent.ToServerRelativeFormUrl(formUrl, WebUrl, SiteUrl).Should().Be(expected);
        }

        [Fact]
        public void ToServerRelativeFormUrl_ServerRelativeAndAbsoluteValues_AreNormalizedToOneShape()
        {
            // A list's DefaultDisplayFormUrl is normally already server-relative...
            PageScanComponent.ToServerRelativeFormUrl("/sites/team/Lists/Announcements/DispForm.aspx", WebUrl, SiteUrl)
                .Should().Be("/sites/team/Lists/Announcements/DispForm.aspx");

            // ...but the User Information List returns an absolute URL, which must not be treated as a
            // web-relative path and appended to the web.
            PageScanComponent.ToServerRelativeFormUrl(
                "https://contoso.sharepoint.com/_layouts/15/userdisp.aspx", WebUrl, SiteUrl)
                .Should().Be("/_layouts/15/userdisp.aspx");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        // An unrecognized token is not a path; resolving it would invent a page location.
        [InlineData("~unknowntoken/Post.aspx")]
        public void ToServerRelativeFormUrl_UnusableValues_AreNull(string formUrl)
        {
            PageScanComponent.ToServerRelativeFormUrl(formUrl, WebUrl, SiteUrl).Should().BeNull();
            PageScanComponent.IsContentPageForm(PageScanComponent.ToServerRelativeFormUrl(formUrl, WebUrl, SiteUrl))
                .Should().BeFalse();
        }

        [Theory]
        // Ordinary data lists stay skipped: this is what keeps the route from reporting every list item in
        // the tenant as a page.
        [InlineData("/sites/team/Lists/Announcements/DispForm.aspx")]
        [InlineData("/sites/team/Lists/Tasks/dispform.aspx")]
        [InlineData("/_layouts/15/userdisp.aspx")]
        [InlineData("/sites/team/_layouts/15/WrkTaskIP.aspx")]
        public void IsStockListForm_SystemForms_AreStock(string formUrl)
        {
            PageScanComponent.IsStockListForm(formUrl).Should().BeTrue();
            PageScanComponent.IsContentPageForm(formUrl).Should().BeFalse();
        }

        [Theory]
        [InlineData("/sites/team/blog/Lists/Posts/Post.aspx")]
        [InlineData("/sites/team/Lists/Requests/CustomDisplay.aspx")]
        public void IsStockListForm_ContentPageForms_AreNotStock(string formUrl)
        {
            PageScanComponent.IsStockListForm(formUrl).Should().BeFalse();
            PageScanComponent.IsContentPageForm(formUrl).Should().BeTrue();
        }

        [Fact]
        public void IsStockListForm_IgnoresQueryString()
        {
            PageScanComponent.IsStockListForm("/sites/team/Lists/Announcements/DispForm.aspx?ID=7")
                .Should().BeTrue();
        }
    }
}
