using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages;

public class PublishingLayoutStatusReaderTests : IClassFixture<ScanContextFixture>
{
    private readonly ScanContextFixture fixture;
    private const string SiteUrl = "https://contoso.sharepoint.com/sites/a";
    private static readonly Uri WebUrl = new("https://contoso.sharepoint.com/sites/a/subweb");
    private const string LayoutUrl = "/sites/a/_catalogs/masterpage/Basic.aspx";

    public PublishingLayoutStatusReaderTests(ScanContextFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("Uncustomized", false)]
    [InlineData("Customized", false)]
    [InlineData("None", false)]
    [InlineData("Uncustomized", true)]
    public async Task Post_scan_uses_root_discovery_committed_after_subwebs_without_requests(string status, bool absoluteDiscoveryUrl)
    {
        var scan = Guid.NewGuid();
        var first = StoredPage(scan, "/first", "https://contoso.sharepoint.com" + LayoutUrl);
        var second = StoredPage(scan, "/second", LayoutUrl.ToLowerInvariant());
        first.CustomizedPageStatus = "Customized";
        first.WebPartCount = 3;
        var wiki = StoredPage(scan, "/wiki", null);
        wiki.PageType = "WikiPage";
        using (var db = fixture.CreateContext())
        {
            db.ClassicPages.AddRange(first, second, wiki);
            await db.SaveChangesAsync();
        }
        using (var db = fixture.CreateContext())
        {
            var layout = Discovered(scan, absoluteDiscoveryUrl ? "https://contoso.sharepoint.com" + LayoutUrl : LayoutUrl, status);
            layout.AssessmentStatus = "NotApplicable";
            db.ClassicPageDiscoveries.Add(layout);
            await db.SaveChangesAsync();
        }

        using (var db = fixture.CreateContext())
        {
            await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
                _ => throw new InvalidOperationException("Discovery should avoid all requests"),
                (error, _) => throw new InvalidOperationException("Unexpected enrichment failure", error));
        }
        using (var db = fixture.CreateContext())
        {
            var pages = await db.ClassicPages.Where(page => page.ScanId == scan).ToListAsync();
            pages.Where(page => page.PageType == "PublishingPage")
                .Should().OnlyContain(page => page.LayoutCustomizedPageStatus == status && page.AssessmentStatus == "Complete");
            var saved = pages.Single(page => page.WebUrl == "/first");
            saved.CustomizedPageStatus.Should().Be("Customized");
            saved.WebPartCount.Should().Be(3);
            saved.LayoutUrl.Should().Be(first.LayoutUrl);
            pages.Single(page => page.PageType == "WikiPage").LayoutCustomizedPageStatus.Should().BeNull();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_discovery_or_status_falls_back_once_across_webs(bool hasDiscovery)
    {
        var scan = Guid.NewGuid();
        using var db = fixture.CreateContext();
        db.ClassicPages.AddRange(StoredPage(scan, "/first", LayoutUrl),
            StoredPage(scan, "/second", "https://contoso.sharepoint.com" + LayoutUrl));
        if (hasDiscovery) db.ClassicPageDiscoveries.Add(Discovered(scan, LayoutUrl, null));
        await db.SaveChangesAsync();
        var requests = new List<string>();

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl, url =>
        {
            requests.Add(url);
            return Task.FromResult("1");
        }, (error, _) => throw new InvalidOperationException("Unexpected enrichment failure", error));

        requests.Should().Equal(LayoutUrl);
        db.ChangeTracker.Clear();
        var pages = await db.ClassicPages.Where(page => page.ScanId == scan).ToListAsync();
        pages.Should().OnlyContain(page => page.LayoutCustomizedPageStatus == "Uncustomized");
    }

    [Theory]
    [InlineData("Customized", 1)]
    [InlineData(null, 2)]
    public async Task Discovery_and_fallback_results_are_scoped_to_scan_and_site_collection(string discoveredStatus, int expectedRequests)
    {
        var scan = Guid.NewGuid();
        var otherScan = Guid.NewGuid();
        const string otherSite = "https://contoso.sharepoint.com/sites/b";
        using var db = fixture.CreateContext();
        var first = StoredPage(scan, "/first", LayoutUrl);
        var second = StoredPage(scan, "/second", LayoutUrl);
        second.SiteUrl = otherSite;
        var untouched = StoredPage(otherScan, "/first", LayoutUrl);
        db.ClassicPages.AddRange(first, second, untouched);
        var otherSiteLayout = Discovered(scan, LayoutUrl, discoveredStatus);
        otherSiteLayout.SiteUrl = otherSite;
        db.ClassicPageDiscoveries.AddRange(otherSiteLayout, Discovered(otherScan, LayoutUrl, "None"));
        await db.SaveChangesAsync();
        var requests = 0;
        Task<string> Read(string _)
        {
            requests++;
            return Task.FromResult("Uncustomized");
        }

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl, Read,
            (error, _) => throw new InvalidOperationException("Unexpected enrichment failure", error));
        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, otherSite, Read,
            (error, _) => throw new InvalidOperationException("Unexpected enrichment failure", error));

        requests.Should().Be(expectedRequests);
        first.LayoutCustomizedPageStatus.Should().Be("Uncustomized");
        second.LayoutCustomizedPageStatus.Should().Be(discoveredStatus ?? "Uncustomized");
        untouched.LayoutCustomizedPageStatus.Should().BeNull();
    }

    [Fact]
    public async Task Conflicting_discovery_statuses_are_reported_without_selecting_one_or_fetching_again()
    {
        var scan = Guid.NewGuid();
        using var db = fixture.CreateContext();
        var page = StoredPage(scan, "/first", LayoutUrl);
        db.ClassicPages.Add(page);
        db.ClassicPageDiscoveries.AddRange(
            Discovered(scan, LayoutUrl, "Uncustomized"), Discovered(scan, LayoutUrl, "Customized"));
        await db.SaveChangesAsync();
        var errors = new List<Exception>();

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new InvalidOperationException("Conflicting observations should not trigger a request"),
            (error, _) => errors.Add(error));

        errors.Should().ContainSingle().Which.Should().BeOfType<InvalidDataException>()
            .Which.Message.Should().Contain("conflicting");
        page.LayoutCustomizedPageStatus.Should().BeNull();
        page.AssessmentStatus.Should().Be("Failed");
    }

    [Fact]
    public async Task Post_scan_failure_preserves_inventory_and_records_each_affected_page()
    {
        var scan = Guid.NewGuid();
        using var db = fixture.CreateContext();
        foreach (var web in new[] { "/first", "/second" })
        {
            var page = StoredPage(scan, web, LayoutUrl);
            page.WebPartCount = 3;
            page.MappingPercentage = 100;
            page.RemediationCode = "CP3";
            db.ClassicPages.Add(page);
            var row = Discovered(scan, page.PageUrl, "Customized");
            row.WebUrl = web;
            row.AssessmentStatus = "Complete";
            db.ClassicPageDiscoveries.Add(row);
        }
        await db.SaveChangesAsync();
        var requests = 0;
        var failures = new List<string>();

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl, _ =>
        {
            requests++;
            throw new UnauthorizedAccessException("Layout denied");
        }, (_, page) => failures.Add(page.PageUrl));

        requests.Should().Be(1);
        failures.Should().HaveCount(2);
        db.ChangeTracker.Clear();
        var pages = await db.ClassicPages.Where(page => page.ScanId == scan).ToListAsync();
        pages.Should().OnlyContain(page => page.AssessmentStatus == "Failed" &&
            page.LayoutCustomizedPageStatus == null && page.WebPartCount == 3 &&
            page.MappingPercentage == 100 && page.RemediationCode == "CP3");
        var rows = await db.ClassicPageDiscoveries.Where(row => row.ScanId == scan).ToListAsync();
        rows.Should().OnlyContain(row => row.AssessmentStatus == "Failed" &&
            row.ErrorStage == "PageLayoutMetadata" && row.ErrorDetail.Contains("Layout denied"));
    }

    [Fact]
    public async Task Post_scan_cancellation_propagates_without_recording_a_layout_failure()
    {
        var scan = Guid.NewGuid();
        using var db = fixture.CreateContext();
        db.ClassicPages.Add(StoredPage(scan, "/first", LayoutUrl));
        await db.SaveChangesAsync();

        var enrich = () => PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new OperationCanceledException(),
            (_, _) => throw new InvalidOperationException("Cancellation is not a layout failure"));
        await enrich.Should().ThrowAsync<OperationCanceledException>();
        db.ChangeTracker.Clear();
        (await db.ClassicPages.SingleAsync(page => page.ScanId == scan)).AssessmentStatus.Should().Be("Complete");
    }

    private static ClassicPage StoredPage(Guid scan, string web, string layout) => new()
    {
        ScanId = scan, SiteUrl = SiteUrl, WebUrl = web, PageUrl = "/sites/a" + web + "/Pages/home.aspx",
        PageType = PageScanComponent.PublishingPage, LayoutUrl = layout, AssessmentStatus = "Complete",
    };

    private static ClassicPageDiscovery Discovered(Guid scan, string url, string status) => new()
    {
        ScanId = scan, SiteUrl = SiteUrl, WebUrl = "/", RecordKey = Guid.NewGuid().ToString(),
        RowType = "Page", Url = url, CustomizedPageStatus = status, DiscoveryStatus = "Discovered",
    };

    [Fact]
    public async Task Shared_layout_is_read_once_for_absolute_and_server_relative_references()
    {
        var requests = new List<string>();
        var reader = new PublishingLayoutStatusReader(WebUrl, url =>
        {
            requests.Add(url);
            return Task.FromResult("1");
        });
        var first = Page("https://contoso.sharepoint.com" + LayoutUrl);
        var second = Page(LayoutUrl);
        first.CustomizedPageStatus = "Customized";

        await reader.EnrichAsync(first);
        await reader.EnrichAsync(second);

        requests.Should().Equal(LayoutUrl);
        first.LayoutCustomizedPageStatus.Should().Be("Uncustomized");
        second.LayoutCustomizedPageStatus.Should().Be("Uncustomized");
        first.CustomizedPageStatus.Should().Be("Customized", "page and layout are separate files");
        first.LayoutUrl.Should().Be("https://contoso.sharepoint.com" + LayoutUrl);
    }

    [Theory]
    [InlineData("WikiPage")]
    [InlineData("WebPartPage")]
    [InlineData("ASPXPage")]
    [InlineData("BlogPage")]
    [InlineData("ModernPage")]
    public async Task Non_publishing_pages_have_null_layout_metadata_without_a_request(string pageType)
    {
        var reader = new PublishingLayoutStatusReader(WebUrl, _ => throw new InvalidOperationException("Unexpected request"));
        var page = Page(LayoutUrl);
        page.PageType = pageType;
        page.LayoutCustomizedPageStatus = "Uncustomized";

        await reader.EnrichAsync(page);

        page.LayoutUrl.Should().BeNull();
        page.LayoutCustomizedPageStatus.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Missing_layout_reference_does_not_infer_a_status(string url)
    {
        var reader = new PublishingLayoutStatusReader(WebUrl, _ => throw new InvalidOperationException("Unexpected request"));
        var page = Page(url);
        await reader.EnrichAsync(page);
        page.LayoutCustomizedPageStatus.Should().BeNull();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("None", "None")]
    [InlineData("2", "Customized")]
    public async Task Layout_status_preserves_unknown_none_and_customized(string returned, string expected)
    {
        var reader = new PublishingLayoutStatusReader(WebUrl, _ => Task.FromResult(returned));
        var page = Page(LayoutUrl);
        await reader.EnrichAsync(page);
        page.LayoutCustomizedPageStatus.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://other.sharepoint.com/sites/a/_catalogs/masterpage/Basic.aspx")]
    [InlineData("//other.sharepoint.com/Basic.aspx")]
    [InlineData("Basic.aspx")]
    public async Task Invalid_layout_url_surfaces_an_error_instead_of_guessing(string url)
    {
        var reader = new PublishingLayoutStatusReader(WebUrl, _ => throw new InvalidOperationException("Unexpected request"));
        var page = Page(url);
        var enrich = () => reader.EnrichAsync(page);
        await enrich.Should().ThrowAsync<InvalidDataException>();
        page.LayoutCustomizedPageStatus.Should().BeNull();
    }

    [Fact]
    public async Task Failed_shared_layout_read_is_cached_and_reported_for_each_page()
    {
        var requests = 0;
        var reader = new PublishingLayoutStatusReader(WebUrl, _ =>
        {
            requests++;
            throw new UnauthorizedAccessException("Layout denied");
        });
        foreach (var page in new[] { Page(LayoutUrl), Page(LayoutUrl) })
        {
            page.WebPartCount = 3;
            var enrich = () => reader.EnrichAsync(page);
            await enrich.Should().ThrowAsync<UnauthorizedAccessException>();
            page.LayoutCustomizedPageStatus.Should().BeNull();
            page.WebPartCount.Should().Be(3);
        }
        requests.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_and_unrecognized_status_are_not_reported_as_success()
    {
        var cancelled = new PublishingLayoutStatusReader(WebUrl, _ => throw new OperationCanceledException());
        var cancel = () => cancelled.EnrichAsync(Page(LayoutUrl));
        await cancel.Should().ThrowAsync<OperationCanceledException>();

        var invalid = new PublishingLayoutStatusReader(WebUrl, _ => Task.FromResult("99"));
        var read = () => invalid.EnrichAsync(Page(LayoutUrl));
        await read.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Different_layouts_and_reader_instances_do_not_share_results()
    {
        var requests = 0;
        Task<string> Read(string url)
        {
            requests++;
            return Task.FromResult(url == LayoutUrl ? "Uncustomized" : "Customized");
        }
        var first = new PublishingLayoutStatusReader(WebUrl, Read);
        var second = new PublishingLayoutStatusReader(WebUrl, Read);
        var custom = Page("/sites/a/_catalogs/masterpage/Custom.aspx");
        await first.EnrichAsync(Page(LayoutUrl));
        await first.EnrichAsync(custom);
        await second.EnrichAsync(Page(LayoutUrl));
        requests.Should().Be(3);
        custom.LayoutCustomizedPageStatus.Should().Be("Customized");
    }

    private static ClassicPage Page(string url) => new()
    {
        PageType = PageScanComponent.PublishingPage,
        LayoutUrl = url,
    };
}
