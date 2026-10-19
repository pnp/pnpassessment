using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages;

/// <summary>
/// Covers how <see cref="PublishingLayoutStatusReader"/> resolves a layout reference whose file
/// name needs percent-encoding.
///
/// Both consumers of the resolved value expect a decoded server-relative URL: discovery stores
/// <c>File.ServerRelativeUrl</c> verbatim from SharePoint REST, and CSOM's
/// <c>GetFileByServerRelativeUrl</c> treats an escape sequence as literal text. Returning the
/// escaped form therefore misses the discovery fast path and then fails the network fallback.
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

    [Theory]
    [InlineData("/sites/a/_catalogs/masterpage/My Layout.aspx",
                "/sites/a/_catalogs/masterpage/My Layout.aspx")]
    [InlineData("/sites/a/_catalogs/masterpage/My%20Layout.aspx",
                "/sites/a/_catalogs/masterpage/My Layout.aspx")]
    [InlineData("/sites/a/_catalogs/masterpage/P\u00e1gina.aspx",
                "/sites/a/_catalogs/masterpage/P\u00e1gina.aspx")]
    [InlineData("https://contoso.sharepoint.com/sites/a/_catalogs/masterpage/P%C3%A1gina.aspx",
                "/sites/a/_catalogs/masterpage/P\u00e1gina.aspx")]
    [InlineData("/sites/a/_catalogs/masterpage/Rapport %26 Analyse.aspx",
                "/sites/a/_catalogs/masterpage/Rapport & Analyse.aspx")]
    public async Task Escaped_layout_names_are_requested_with_the_decoded_server_relative_url(
        string layoutUrl, string expected)
    {
        string requested = null;
        var reader = new PublishingLayoutStatusReader(SiteUri, url =>
        {
            requested = url;
            return Task.FromResult("Uncustomized");
        });

        await reader.EnrichAsync(Page(layoutUrl));

        requested.Should().Be(expected);
    }

    [Theory]
    [InlineData("/sites/a/_catalogs/masterpage/My Layout.aspx", "/sites/a/_catalogs/masterpage/My%20Layout.aspx")]
    [InlineData("/sites/a/_catalogs/masterpage/P\u00e1gina.aspx", "/sites/a/_catalogs/masterpage/P%C3%A1gina.aspx")]
    public async Task Encoded_and_decoded_spellings_of_one_layout_share_a_single_read(
        string first, string second)
    {
        var requests = new List<string>();
        var reader = new PublishingLayoutStatusReader(SiteUri, url =>
        {
            requests.Add(url);
            return Task.FromResult("Uncustomized");
        });

        await reader.EnrichAsync(Page(first));
        await reader.EnrichAsync(Page(second));

        requests.Should().ContainSingle("both spellings denote the same file");
    }

    [Theory]
    [InlineData("/sites/a/%252e%252e/OTHER/x.aspx")]
    [InlineData("/sites/a/%252f%252e%252e/x.aspx")]
    [InlineData("/sites/a/%255c..%255cx.aspx")]
    [InlineData("https://contoso.sharepoint.com/sites/a/%252e%252e/OTHER/x.aspx")]
    [InlineData("/sites/a/_catalogs/masterpage/%2525.aspx")]
    public async Task Multiply_encoded_layout_url_is_rejected_instead_of_being_passed_on(string layoutUrl)
    {
        var reader = new PublishingLayoutStatusReader(SiteUri,
            _ => throw new InvalidOperationException("A multiply-encoded reference must never be requested."));

        var enrich = () => reader.EnrichAsync(Page(layoutUrl));

        await enrich.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Escaped_layout_name_matches_discovery_without_a_fallback_request()
    {
        const string decoded = "/sites/a/_catalogs/masterpage/My Layout.aspx";
        var scan = Guid.NewGuid();
        using var db = fixture.CreateContext();
        db.ClassicPages.Add(new ClassicPage
        {
            ScanId = scan,
            SiteUrl = SiteUrl,
            WebUrl = "/",
            PageUrl = "/sites/a/Pages/home.aspx",
            PageType = PageScanComponent.PublishingPage,
            LayoutUrl = decoded,
            AssessmentStatus = "Complete",
        });

        // Discovery records the SharePoint ServerRelativeUrl, which is not percent-encoded.
        db.ClassicPageDiscoveries.Add(new ClassicPageDiscovery
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

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new InvalidOperationException("Discovery already holds this layout; no fallback is expected."),
            (error, _) => throw new InvalidOperationException("Unexpected enrichment failure", error));

        db.ChangeTracker.Clear();
        var page = await db.ClassicPages.SingleAsync(value => value.ScanId == scan);
        page.LayoutCustomizedPageStatus.Should().Be("Uncustomized");
    }

    private static ClassicPage Page(string layoutUrl) => new()
    {
        PageType = PageScanComponent.PublishingPage,
        LayoutUrl = layoutUrl,
    };
}
