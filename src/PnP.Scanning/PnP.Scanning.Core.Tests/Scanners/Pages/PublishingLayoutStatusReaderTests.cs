using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages;

public class PublishingLayoutStatusReaderTests : IClassFixture<ScanContextFixture>
{
    private readonly ScanContextFixture fixture;
    private const string SiteUrl = "https://contoso.sharepoint.com/sites/a";
    private static readonly Uri SiteUri = new(SiteUrl);
    private const string LayoutUrl = "/sites/a/_catalogs/masterpage/Basic.aspx";

    public PublishingLayoutStatusReaderTests(ScanContextFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("Uncustomized", false)]
    [InlineData("None", true)]
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
        second.LayoutUrl = "/sites/b/_catalogs/masterpage/Basic.aspx";
        var untouched = StoredPage(otherScan, "/first", LayoutUrl);
        db.ClassicPages.AddRange(first, second, untouched);
        var otherSiteLayout = Discovered(scan, second.LayoutUrl, discoveredStatus);
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
    public async Task Discovery_query_only_materializes_referenced_layouts_and_failed_pages()
    {
        var scan = Guid.NewGuid();
        using var db = fixture.CreateContext();
        var page = StoredPage(scan, "/first", "https://contoso.sharepoint.com" + LayoutUrl.ToLowerInvariant());
        var invalid = StoredPage(scan, "/second", "/sites/other/_catalogs/masterpage/Basic.aspx");
        db.ClassicPages.AddRange(page, invalid);
        var layout = Discovered(scan, LayoutUrl, "Uncustomized");
        var pageRow = Discovered(scan, page.PageUrl, "Customized");
        pageRow.WebUrl = page.WebUrl;
        var invalidRow = Discovered(scan, invalid.PageUrl.ToUpperInvariant(), "Customized");
        invalidRow.WebUrl = invalid.WebUrl;
        db.ClassicPageDiscoveries.AddRange(layout, pageRow, invalidRow,
            Discovered(scan, invalid.LayoutUrl, "Customized"));
        db.ClassicPageDiscoveries.AddRange(Enumerable.Range(0, 2000).Select(index =>
            Discovered(scan, $"/sites/a/unrelated/{index}.aspx", "Uncustomized")));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var errors = new List<Exception>();

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new InvalidOperationException("No fallback should be needed"),
            (error, _) => errors.Add(error));

        errors.Should().ContainSingle().Which.Should().BeOfType<InvalidDataException>();
        // The prefetch covers layouts only. A page's own discovery row is read solely to carry a
        // failure, so the successfully enriched page never materializes one.
        db.ChangeTracker.Entries<ClassicPageDiscovery>().Select(entry => entry.Entity.RecordKey)
            .Should().BeEquivalentTo(new[] { layout.RecordKey, invalidRow.RecordKey });
        db.ChangeTracker.Entries<ClassicPageDiscovery>().Select(entry => entry.Entity.RecordKey)
            .Should().NotContain(pageRow.RecordKey);
        (await db.ClassicPages.SingleAsync(value => value.ScanId == scan && value.PageUrl == page.PageUrl))
            .LayoutCustomizedPageStatus.Should().Be("Uncustomized");
        (await db.ClassicPageDiscoveries.FindAsync(scan, invalidRow.RecordKey))
            .ErrorStage.Should().Be("PageLayoutMetadata");
    }

    [Theory]
    [InlineData("Uncustomized", "Customized")]
    [InlineData("Customized", "Uncustomized")]
    [InlineData("Customized", "None")]
    public async Task Conflicting_discovery_retains_latest_status_and_records_changed_during_scan(string previous, string current)
    {
        var scan = Guid.NewGuid();
        using var db = fixture.CreateContext();
        db.ClassicPages.AddRange(StoredPage(scan, "/first", LayoutUrl),
            StoredPage(scan, "/second", "https://contoso.sharepoint.com" + LayoutUrl));
        var older = Discovered(scan, LayoutUrl, previous);
        older.ObservedAtUtc = new DateTime(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc);
        var latest = Discovered(scan, "https://contoso.sharepoint.com" + LayoutUrl, current);
        latest.ObservedAtUtc = older.ObservedAtUtc.AddMinutes(1);
        latest.ErrorCodes = "existing_evidence";
        db.ClassicPageDiscoveries.AddRange(latest, older);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new InvalidOperationException("Conflicting observations should not trigger a request"),
            (error, _) => throw new InvalidOperationException("Discovery changes are not layout failures", error));

        db.ChangeTracker.Clear();
        var pages = await db.ClassicPages.Where(page => page.ScanId == scan).ToListAsync();
        pages.Should().HaveCount(2).And.OnlyContain(page =>
            page.LayoutCustomizedPageStatus == current && page.AssessmentStatus == "Complete");
        var saved = await db.ClassicPageDiscoveries.FindAsync(scan, latest.RecordKey);
        saved.CustomizedPageStatus.Should().Be(current);
        saved.DiscoveryStatus.Should().Be("Discovered");
        saved.ErrorStage.Should().Be("DiscoveryMetadata");
        saved.ErrorCodes.Should().Contain(DiscoveryGapCodes.ChangedDuringScan).And.Contain("existing_evidence");
        saved.ErrorDetail.Should().Contain("latest observed value " + current);
    }

    [Theory]
    [InlineData("Complete")]
    [InlineData("Failed")]
    public async Task Post_scan_failure_preserves_assessment_status_and_inventory(string assessmentStatus)
    {
        var scan = Guid.NewGuid();
        using var db = fixture.CreateContext();
        foreach (var web in new[] { "/first", "/second" })
        {
            var page = StoredPage(scan, web, LayoutUrl);
            page.WebPartCount = 3;
            page.MappingPercentage = 100;
            page.RemediationCode = "CP3";
            page.AssessmentStatus = assessmentStatus;
            db.ClassicPages.Add(page);
            var row = Discovered(scan, page.PageUrl, "Customized");
            row.WebUrl = web;
            row.AssessmentStatus = assessmentStatus;
            if (assessmentStatus == "Failed")
            {
                row.ErrorStage = "WebPartAssessment";
                row.ErrorCodes = "ExistingFailure";
                row.ErrorDetail = "Original page assessment failed";
            }
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
        pages.Should().OnlyContain(page => page.AssessmentStatus == assessmentStatus &&
            page.LayoutCustomizedPageStatus == null && page.WebPartCount == 3 &&
            page.MappingPercentage == 100 && page.RemediationCode == "CP3");
        var rows = await db.ClassicPageDiscoveries.Where(row => row.ScanId == scan).ToListAsync();
        rows.Should().OnlyContain(row => row.AssessmentStatus == assessmentStatus &&
            row.ErrorStage.Contains("PageLayoutMetadata") && row.ErrorDetail.Contains("Layout denied"));
        if (assessmentStatus == "Failed")
            rows.Should().OnlyContain(row => row.ErrorStage.Contains("WebPartAssessment") &&
                row.ErrorCodes.Contains("ExistingFailure") && row.ErrorDetail.Contains("Original page assessment failed"));
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Missing_layout_reference_does_not_infer_a_status(string url)
    {
        var reader = new PublishingLayoutStatusReader(SiteUri, _ => throw new InvalidOperationException("Unexpected request"));
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
        var reader = new PublishingLayoutStatusReader(SiteUri, _ => Task.FromResult(returned));
        var page = Page(LayoutUrl);
        await reader.EnrichAsync(page);
        page.LayoutCustomizedPageStatus.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://other.sharepoint.com/sites/a/_catalogs/masterpage/Basic.aspx")]
    [InlineData("//other.sharepoint.com/Basic.aspx")]
    [InlineData("Basic.aspx")]
    [InlineData("/sites/OTHER/_catalogs/masterpage/Basic.aspx")]
    [InlineData("https://contoso.sharepoint.com/sites/OTHER/_catalogs/masterpage/Basic.aspx")]
    [InlineData("/sites/ab/_catalogs/masterpage/Basic.aspx")]
    [InlineData("/sites/a/../OTHER/_catalogs/masterpage/Basic.aspx")]
    [InlineData("/sites/a/%2e%2e/OTHER/_catalogs/masterpage/Basic.aspx")]
    [InlineData("/sites/a/%2e%2e%2fOTHER/_catalogs/masterpage/Basic.aspx")]
    [InlineData("/sites/a/_catalogs/masterpage/Basic.aspx?other=1")]
    [InlineData("/sites/a/_catalogs/masterpage/Basic.aspx#fragment")]
    public async Task Invalid_layout_url_surfaces_an_error_instead_of_guessing(string url)
    {
        var reader = new PublishingLayoutStatusReader(SiteUri, _ => throw new InvalidOperationException("Unexpected request"));
        var page = Page(url);
        var enrich = () => reader.EnrichAsync(page);
        await enrich.Should().ThrowAsync<InvalidDataException>();
        page.LayoutCustomizedPageStatus.Should().BeNull();
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com", "/_catalogs/masterpage/Basic.aspx")]
    [InlineData("https://contoso.sharepoint.com/sites/a/", "/sites/a/_catalogs/masterpage/Basic.aspx")]
    [InlineData("https://contoso.sharepoint.com/sites/A", "/sites/a/_catalogs/masterpage/Basic.aspx")]
    public async Task Layout_containment_accepts_root_sites_trailing_slashes_and_case_variations(string site, string layout)
    {
        var reader = new PublishingLayoutStatusReader(new Uri(site), _ => Task.FromResult("Uncustomized"));
        var page = Page(layout);
        await reader.EnrichAsync(page);
        page.LayoutCustomizedPageStatus.Should().Be("Uncustomized");
    }

    [Fact]
    public async Task Unrecognized_status_is_not_reported_as_success()
    {
        var invalid = new PublishingLayoutStatusReader(SiteUri, _ => Task.FromResult("99"));
        var read = () => invalid.EnrichAsync(Page(LayoutUrl));
        await read.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Different_layouts_do_not_share_results()
    {
        var requests = 0;
        Task<string> Read(string url)
        {
            requests++;
            return Task.FromResult(url == LayoutUrl ? "Uncustomized" : "Customized");
        }
        var reader = new PublishingLayoutStatusReader(SiteUri, Read);
        var custom = Page("/sites/a/_catalogs/masterpage/Custom.aspx");
        await reader.EnrichAsync(Page(LayoutUrl));
        await reader.EnrichAsync(custom);
        requests.Should().Be(2);
        custom.LayoutCustomizedPageStatus.Should().Be("Customized");
    }

    private static ClassicPage Page(string url) => new()
    {
        PageType = PageScanComponent.PublishingPage,
        LayoutUrl = url,
    };
}
