using PnP.Scanning.Core.ClassicPages.Compatibility.Legacy;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages;

/// <summary>
/// Covers how <see cref="PublishingLayoutStatusReader"/> resolves a layout reference whose file
/// name needs percent-encoding.
///
/// Discovery stores <c>File.ServerRelativeUrl</c> verbatim from SharePoint REST, so resolving
/// a reference to its escaped form misses the discovery fast path. Use one decoded canonical
/// spelling for discovery lookup, the shared-layout cache key, and the CSOM fallback argument.
/// Live SharePoint accepted escaped fallback arguments in the test environment as well; these
/// tests establish the decoded URL contract, not a claim that escaped CSOM requests must fail.
///
/// Decoding is only safe while it is idempotent. The containment checks run on a single decode,
/// so a multiply-encoded reference such as <c>%252e%252e</c> survives them; handing the
/// once-decoded <c>%2e%2e</c> downstream would let a further decode escape the site collection.
/// </summary>
public class PublishingLayoutUrlEncodingTests : IClassFixture<ScanContextFixture>
{
    private const string SiteUrl = "https://contoso.sharepoint.com/sites/a";
    private static readonly Uri SiteUri = new(SiteUrl);

    private readonly ScanContextFixture fixture;

    public PublishingLayoutUrlEncodingTests(ScanContextFixture fixture) => this.fixture = fixture;

    /// <summary>Verifies escaped layout names are requested with the decoded server relative url.</summary>
    [Theory]
    [InlineData("/sites/a/_catalogs/masterpage/My Layout.aspx", "/sites/a/_catalogs/masterpage/My Layout.aspx")]
    [InlineData("/sites/a/_catalogs/masterpage/My%20Layout.aspx", "/sites/a/_catalogs/masterpage/My Layout.aspx")]
    [InlineData("/sites/a/_catalogs/masterpage/P\u00e1gina.aspx", "/sites/a/_catalogs/masterpage/P\u00e1gina.aspx")]
    [InlineData(
        "https://contoso.sharepoint.com/sites/a/_catalogs/masterpage/P%C3%A1gina.aspx",
        "/sites/a/_catalogs/masterpage/P\u00e1gina.aspx")]
    [InlineData(
        "/sites/a/_catalogs/masterpage/Rapport %26 Analyse.aspx",
        "/sites/a/_catalogs/masterpage/Rapport & Analyse.aspx")]
    public async Task Escaped_layout_names_are_requested_with_the_decoded_server_relative_url(
        string layoutUrl,
        string expected)
    {
        string requested = null;
        PublishingLayoutStatusReader reader = new PublishingLayoutStatusReader(
            SiteUri,
            url =>
            {
                requested = url;
                return Task.FromResult("Uncustomized");
            });

        await reader.EnrichAsync(Page(layoutUrl));

        requested.Should().Be(expected);
    }

    /// <summary>Verifies encoded and decoded spellings of one layout share a single read.</summary>
    [Theory]
    [InlineData("/sites/a/_catalogs/masterpage/My Layout.aspx", "/sites/a/_catalogs/masterpage/My%20Layout.aspx")]
    [InlineData("/sites/a/_catalogs/masterpage/P\u00e1gina.aspx", "/sites/a/_catalogs/masterpage/P%C3%A1gina.aspx")]
    public async Task Encoded_and_decoded_spellings_of_one_layout_share_a_single_read(string first, string second)
    {
        List<string> requests = new List<string>();
        PublishingLayoutStatusReader reader = new PublishingLayoutStatusReader(
            SiteUri,
            url =>
            {
                requests.Add(url);
                return Task.FromResult("Uncustomized");
            });

        await reader.EnrichAsync(Page(first));
        await reader.EnrichAsync(Page(second));

        requests.Should().ContainSingle("both spellings denote the same file");
    }

    /// <summary>Verifies multiply encoded layout url is rejected instead of being passed on.</summary>
    [Theory]
    [InlineData("/sites/a/%252e%252e/OTHER/x.aspx")]
    [InlineData("/sites/a/%252f%252e%252e/x.aspx")]
    [InlineData("/sites/a/%255c..%255cx.aspx")]
    [InlineData("https://contoso.sharepoint.com/sites/a/%252e%252e/OTHER/x.aspx")]
    [InlineData("/sites/a/_catalogs/masterpage/%2525.aspx")]
    public async Task Multiply_encoded_layout_url_is_rejected_instead_of_being_passed_on(string layoutUrl)
    {
        PublishingLayoutStatusReader reader = new PublishingLayoutStatusReader(
            SiteUri,
            _ => throw new InvalidOperationException("A multiply-encoded reference must never be requested."));

        Func<Task> enrich = () => reader.EnrichAsync(Page(layoutUrl));

        await enrich.Should().ThrowAsync<InvalidDataException>();
    }

    /// <summary>Verifies escaped layout name matches discovery without a fallback request.</summary>
    [Fact]
    public async Task Escaped_layout_name_matches_discovery_without_a_fallback_request()
    {
        const string decoded = "/sites/a/_catalogs/masterpage/My Layout.aspx";
        Guid scan = Guid.NewGuid();
        using ScanContext db = fixture.CreateContext();
        db.ClassicPages.Add(
            new ClassicPage
            {
                ScanId = scan,
                SiteUrl = SiteUrl,
                WebUrl = "/",
                PageUrl = "/sites/a/Pages/home.aspx",
                PageType = ClassicPageRules.PublishingPage,
                LayoutUrl = decoded,
                AssessmentStatus = "Complete",
            });

        // Discovery records the SharePoint ServerRelativeUrl, which is not percent-encoded.
        db.ClassicPageDiscoveries.Add(
            new ClassicPageDiscovery
            {
                ScanId = scan,
                SiteUrl = SiteUrl,
                WebUrl = "/",
                RecordKey = "layout:" + scan,
                RowType = "Page",
                Url = decoded,
                CustomizedPageStatus = "Uncustomized",
                DiscoveryStatus = "Discovered",
                ObservedAtUtc = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();

        await PublishingLayoutStatusReader.EnrichSiteAsync(
            db,
            scan,
            SiteUrl,
            _ => throw new InvalidOperationException("Discovery already holds this layout; no fallback is expected."),
            (error, _) => throw new InvalidOperationException("Unexpected enrichment failure", error));

        db.ChangeTracker.Clear();
        ClassicPage page = await db.ClassicPages.SingleAsync(value => value.ScanId == scan);
        page.LayoutCustomizedPageStatus.Should().Be("Uncustomized");
    }

    /// <summary>Builds a synthetic stored publishing page for the layout compatibility fixture.</summary>
    private static ClassicPage Page(string layoutUrl) =>
        new() { PageType = ClassicPageRules.PublishingPage, LayoutUrl = layoutUrl, };
}