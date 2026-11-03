using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery
{
    /// <summary>
    /// List and registered content-type form declarations arrive in different URL shapes.
    /// Normalize them before writing evidence, without deriving item pages or counts.
    /// </summary>
    public class DisplayFormUrlPolicyTests
    {
        private const string WebUrl = "/sites/team/blog";
        private const string SiteUrl = "/sites/team";

        /// <summary>
        /// Resolves a stock blog content-type form relative to its owning web rather than the site-collection root.
        /// </summary>
        [Fact]
        public void ToServerRelative_StockBlogPostContentType_NormalizesWebRelativePath()
        {
            string resolved = DisplayFormUrlPolicy.ToServerRelative("Lists/Posts/Post.aspx", WebUrl, SiteUrl);

            resolved.Should().Be("/sites/team/blog/Lists/Posts/Post.aspx");
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

        /// <summary>
        /// Rejects missing, blank, and unknown-token form declarations instead of inventing a normalized location.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        // An unrecognized token is not a path; normalizing it would invent a location.
        [InlineData("~unknowntoken/Post.aspx")]
        public void ToServerRelative_UnusableValues_AreNull(string formUrl)
        {
            DisplayFormUrlPolicy.ToServerRelative(formUrl, WebUrl, SiteUrl).Should().BeNull();
        }
    }
}
