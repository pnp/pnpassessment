using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery
{
    /// <summary>
    /// Display form resolution for the item-only page route (the display-form discovery surface). The route resolves a
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
    public class DisplayFormUrlPolicyTests
    {
        private const string WebUrl = "/sites/team/blog";
        private const string SiteUrl = "/sites/team";

        [Fact]
        public void ToServerRelative_StockBlogPostContentType_ResolvesAndCountsAsContentPage()
        {
            // The regression. SharePoint stores the blog Post content type's display form web-relative, with
            // no leading slash. Discarding it collapsed resolution onto the list default (DispForm.aspx),
            // which the stock-form heuristic skips, so every list in every tenant was skipped.
            string resolved = DisplayFormUrlPolicy.ToServerRelative("Lists/Posts/Post.aspx", WebUrl, SiteUrl);

            resolved.Should().Be("/sites/team/blog/Lists/Posts/Post.aspx");
            DisplayFormUrlPolicy.IsContentPageForm(resolved).Should().BeTrue();
        }

        [Fact]
        public void ToServerRelative_WebRelativeValue_OnRootWeb_DoesNotDoubleSlash()
        {
            DisplayFormUrlPolicy.ToServerRelative("Lists/Posts/Post.aspx", "/", "/")
                .Should().Be("/Lists/Posts/Post.aspx");
        }

        [Theory]
        // Tokens ListFormWebPart.ComputeServerRelativePageUrl expands. "~sitecollectionlayouts" has to be
        // matched before "~sitecollection", which is a prefix of it.
        [InlineData("~site/Lists/Posts/Post.aspx", "/sites/team/blog/Lists/Posts/Post.aspx")]
        [InlineData("~sitecollection/Lists/Posts/Post.aspx", "/sites/team/Lists/Posts/Post.aspx")]
        [InlineData("~layouts/WrkTaskIP.aspx", "/sites/team/blog/_layouts/15/WrkTaskIP.aspx")]
        [InlineData("~sitecollectionlayouts/WrkTaskIP.aspx", "/sites/team/_layouts/15/WrkTaskIP.aspx")]
        public void ToServerRelative_ExpandsTokens(string formUrl, string expected)
        {
            DisplayFormUrlPolicy.ToServerRelative(formUrl, WebUrl, SiteUrl).Should().Be(expected);
        }

        [Fact]
        public void ToServerRelative_ServerRelativeAndAbsoluteValues_AreNormalizedToOneShape()
        {
            // A list's DefaultDisplayFormUrl is normally already server-relative...
            DisplayFormUrlPolicy.ToServerRelative("/sites/team/Lists/Announcements/DispForm.aspx", WebUrl, SiteUrl)
                .Should().Be("/sites/team/Lists/Announcements/DispForm.aspx");

            // ...but the User Information List returns an absolute URL, which must not be treated as a
            // web-relative path and appended to the web.
            DisplayFormUrlPolicy.ToServerRelative(
                "https://contoso.sharepoint.com/_layouts/15/userdisp.aspx", WebUrl, SiteUrl)
                .Should().Be("/_layouts/15/userdisp.aspx");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        // An unrecognized token is not a path; resolving it would invent a page location.
        [InlineData("~unknowntoken/Post.aspx")]
        public void ToServerRelative_UnusableValues_AreNull(string formUrl)
        {
            DisplayFormUrlPolicy.ToServerRelative(formUrl, WebUrl, SiteUrl).Should().BeNull();
            DisplayFormUrlPolicy.IsContentPageForm(DisplayFormUrlPolicy.ToServerRelative(formUrl, WebUrl, SiteUrl))
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
            DisplayFormUrlPolicy.IsStockListForm(formUrl).Should().BeTrue();
            DisplayFormUrlPolicy.IsContentPageForm(formUrl).Should().BeFalse();
        }

        [Theory]
        [InlineData("/sites/team/blog/Lists/Posts/Post.aspx")]
        [InlineData("/sites/team/Lists/Requests/CustomDisplay.aspx")]
        public void IsStockListForm_ContentPageForms_AreNotStock(string formUrl)
        {
            DisplayFormUrlPolicy.IsStockListForm(formUrl).Should().BeFalse();
            DisplayFormUrlPolicy.IsContentPageForm(formUrl).Should().BeTrue();
        }

        [Fact]
        public void IsStockListForm_IgnoresQueryString()
        {
            DisplayFormUrlPolicy.IsStockListForm("/sites/team/Lists/Announcements/DispForm.aspx?ID=7")
                .Should().BeTrue();
        }
    }
}
