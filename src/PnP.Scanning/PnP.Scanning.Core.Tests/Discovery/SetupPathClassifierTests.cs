using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

/// <summary>
/// Setup-path classification. The server keys on suffix matches because real <c>vti_setuppath</c> values
/// carry a provisioning prefix, so a bare file-name comparison would miss every ghosted page.
/// </summary>
public class SetupPathClassifierTests
{
    [Fact]
    public void Known_template_suffix_is_classified()
    {
        // Every list view ghosts from one viewpage.aspx, every list form from one form.aspx.
        Classify(@"pages\viewpage.aspx").Should().Be(SetupPathPageFamily.ListView);
        Classify(@"pages\form.aspx").Should().Be(SetupPathPageFamily.ListForm);
        // Observed live: the real values are prefixed, so only the suffix matches.
        Classify(@"DocumentTemplates\wkpstd.aspx").Should().Be(SetupPathPageFamily.WikiPage);
        Classify(@"1033\STS\doctemp\smartpgs\spstd1.aspx").Should().Be(SetupPathPageFamily.WebPartPage);
        Classify(@"1033\STS\doctemp\smartpgs\spstd3.aspx").Should().Be(SetupPathPageFamily.WebPartPage);
        Classify(@"1033\STS\doctemp\smartpgs\spstd8.aspx").Should().Be(SetupPathPageFamily.WebPartPage);
        Classify(@"1033\STS\doctemp\smartpgs\bpstd.aspx").Should().Be(SetupPathPageFamily.WebPartPage);
        Classify(@"Features\BlogContent\Post.aspx").Should().Be(SetupPathPageFamily.BlogPost);
    }

    [Fact]
    public void Classification_ignores_case()
    {
        Classify(@"documenttemplates\WKPSTD.ASPX").Should().Be(SetupPathPageFamily.WikiPage);
        Classify(@"PAGES\FORM.ASPX").Should().Be(SetupPathPageFamily.ListForm);
    }

    [Fact]
    public void Missing_setup_path_is_unghosted()
    {
        Classify(null).Should().Be(SetupPathPageFamily.Unghosted);
        Classify("").Should().Be(SetupPathPageFamily.Unghosted);
        Classify("   ").Should().Be(SetupPathPageFamily.Unghosted);
    }

    [Fact]
    public void Unrecognised_but_present_setup_path_is_unknown_not_unghosted()
    {
        // Custom site definitions and third-party provisioning ghost from templates the server does not know.
        Classify(@"Features\SitePagePublishing\Home.aspx").Should().Be(SetupPathPageFamily.Unknown);
        Classify(@"SiteTemplates\STS\default.aspx").Should().Be(SetupPathPageFamily.Unknown);
        // 'myform.aspx' only means a list form when it ghosts from the pages\form.aspx template.
        Classify(@"contoso\templates\myform.aspx").Should().Be(SetupPathPageFamily.Unknown);
    }

    private static SetupPathPageFamily Classify(string setupPath) => SetupPathClassifier.Classify(setupPath);
}
