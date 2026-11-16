using PnP.Scanning.Core.ClassicPages.Collection.Page;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages;

/// <summary>Verifies which server paths are passed to SharePoint as decoded paths.</summary>
public sealed class CsomFileLocatorTests
{
    /// <summary>
    /// A hash, or a percent sign that is part of a name, selects the decoded path; every other path, including one
    /// with URL escapes, keeps the existing request.
    /// </summary>
    [Theory]
    [InlineData("/sites/team/SitePages/Home.aspx", false)]
    [InlineData("/sites/team/My Sub/SitePages/Home.aspx", false)]
    [InlineData("/sites/team/My%20Sub/SitePages/Home.aspx", false)]
    [InlineData("/sites/team/SitePages/C# notes.aspx", true)]
    [InlineData("/sites/team/SitePages/100% done.aspx", true)]
    [InlineData("/sites/team/SitePages/Q3 #1 100% done.aspx", true)]
    public void A_hash_or_a_percent_sign_in_a_name_selects_the_decoded_path(string path, bool expected) =>
        Assert.Equal(expected, CsomFileLocator.NeedsDecodedPath(path));
}
