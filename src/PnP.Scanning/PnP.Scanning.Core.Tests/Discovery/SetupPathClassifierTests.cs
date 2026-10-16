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

    /// <summary>
    /// A web's root default.aspx ghosts from a site-definition home page template. These are the web part
    /// page layouts the server itself treats as convertible, so missing them leaves the entry page of every
    /// team site, document workspace and project site classified as an unrecognised template.
    /// </summary>
    [Theory]
    [InlineData(@"SiteTemplates\STS\default.aspx")]
    [InlineData(@"SiteTemplates\STS\defaultdws.aspx")]
    [InlineData(@"SiteTemplates\PROJECTSITE\default.aspx")]
    [InlineData(@"Features\BDR\default.aspx")]
    public void Site_definition_home_page_template_is_a_web_part_page(string setupPath)
    {
        Classify(setupPath).Should().Be(SetupPathPageFamily.WebPartPage);
        // Observed live values carry no prefix, but the server's own paths do, so both must match.
        Classify(@"15\TEMPLATE\" + setupPath).Should().Be(SetupPathPageFamily.WebPartPage);
    }

    [Fact]
    public void Default_and_defaultdws_templates_do_not_collide()
    {
        // 'defaultdws.aspx' must not be swallowed by the 'default.aspx' suffix, and neither may claim a
        // same-named page from an unrelated site definition.
        Classify(@"SiteTemplates\STS\defaultdws.aspx").Should().Be(SetupPathPageFamily.WebPartPage);
        Classify(@"SiteTemplates\CONTOSO\default.aspx").Should().Be(SetupPathPageFamily.Unknown);
        Classify(@"Features\CONTOSO\default.aspx").Should().Be(SetupPathPageFamily.Unknown);
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
        Classify(@"Features\GroupHomepage\Home.aspx").Should().Be(SetupPathPageFamily.Unknown);
        // 'myform.aspx' only means a list form when it ghosts from the pages\form.aspx template.
        Classify(@"contoso\templates\myform.aspx").Should().Be(SetupPathPageFamily.Unknown);
    }

    private static SetupPathPageFamily Classify(string setupPath) => SetupPathClassifier.Classify(setupPath);
}
