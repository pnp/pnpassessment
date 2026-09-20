using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class AssessmentRestartEvidenceTests : IClassFixture<ScanContextFixture>
{
    private const string Site = "https://contoso.sharepoint.com/sites/restart";
    private readonly ScanContextFixture database;
    public AssessmentRestartEvidenceTests(ScanContextFixture database) => this.database = database;
    private AssessmentDiscoveryWriter Writer() => new(database.CreateContext);

    [Fact]
    public async Task Native_restart_projection_preserves_templates_and_only_returns_this_sites_queued_webs()
    {
        var scan = Guid.NewGuid();
        using var db = database.CreateContext();
        db.Webs.AddRange(
            Web(scan, Site, "/", "SITEPAGEPUBLISHING#0", SiteWebStatus.Finished),
            Web(scan, Site, "/team", "STS#0", SiteWebStatus.Queued),
            Web(scan, Site, "/wiki", "ENTERWIKI#0", SiteWebStatus.Queued),
            Web(scan, Site, "/unknown", null, SiteWebStatus.Queued),
            Web(scan, Site, "/failed", "STS#0", SiteWebStatus.Failed),
            Web(scan, Site + "-other", "/other-site", "STS#0", SiteWebStatus.Queued),
            Web(Guid.NewGuid(), Site, "/other-scan", "STS#0", SiteWebStatus.Queued));
        await db.SaveChangesAsync();

        var pending = await StorageManager.WebsToRestartScanningAsync(db, scan, Site);
        pending.Select(web => (web.WebUrl, web.WebTemplate)).Should().BeEquivalentTo(new[]
        {
            ("/team", "STS#0"), ("/wiki", "ENTERWIKI#0"), ("/unknown", (string)null),
        });
        (await db.Webs.CountAsync(web => web.ScanId == scan && web.SiteUrl == Site && web.Status == SiteWebStatus.Queued))
            .Should().Be(3, "reading the restart queue must not mutate work state");
    }

    [Fact]
    public async Task Pending_restart_subset_does_not_replace_full_site_authority_counts_or_observation_time()
    {
        var scan = Guid.NewGuid();
        var all = Enumeration(8);
        await ClassicPageDiscoveryComponent.RecordWebEnumerationScopeAsync(scan, Site, all, writer: Writer());
        var original = await Scope(scan);
        await ClassicPageDiscoveryComponent.RecordWebEnumerationScopeAsync(scan, Site, Enumeration(4, replay: true), writer: Writer());
        await ClassicPageDiscoveryComponent.RecordWebEnumerationScopeAsync(scan, Site, Enumeration(2, replay: true), writer: Writer());
        var retained = await Scope(scan);
        retained.Should().BeEquivalentTo(original);
        retained.ExpectedChildCount.Should().Be(8);
        retained.ObservedChildCount.Should().Be(8);
    }

    [Theory]
    [InlineData("Denied")]
    [InlineData("Failed")]
    public async Task Restart_replay_does_not_turn_incomplete_web_authority_into_complete(string status)
    {
        var scan = Guid.NewGuid();
        Exception error = status == "Denied" ? new UnauthorizedAccessException("subweb authority denied") : new IOException("enumeration interrupted");
        await ClassicPageDiscoveryComponent.RecordWebEnumerationScopeAsync(scan, Site, Enumeration(1), error, Writer());
        var original = await Scope(scan);
        original.DiscoveryStatus.Should().Be(status);
        original.ExpectedChildCount.Should().BeNull();
        original.ErrorCodes.Should().NotBeNullOrEmpty();

        await ClassicPageDiscoveryComponent.RecordWebEnumerationScopeAsync(scan, Site, Enumeration(1, replay: true), writer: Writer());
        (await Scope(scan)).Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task Legacy_checkpoint_without_authority_does_not_invent_a_complete_enumeration()
    {
        var scan = Guid.NewGuid();
        await ClassicPageDiscoveryComponent.RecordWebEnumerationScopeAsync(scan, Site, Enumeration(4, replay: true), writer: Writer());
        using var db = database.CreateContext();
        (await db.ClassicPageDiscoveries.CountAsync(row => row.ScanId == scan)).Should().Be(0);
    }

    [Fact]
    public async Task Fresh_enumeration_is_still_recorded_including_an_empty_result()
    {
        var scan = Guid.NewGuid();
        await ClassicPageDiscoveryComponent.RecordWebEnumerationScopeAsync(scan, Site, Enumeration(3), writer: Writer());
        (await Scope(scan)).ExpectedChildCount.Should().Be(3);
        await ClassicPageDiscoveryComponent.RecordWebEnumerationScopeAsync(scan, Site, Enumeration(0), writer: Writer());
        var empty = await Scope(scan);
        empty.ExpectedChildCount.Should().Be(0);
        empty.ObservedChildCount.Should().Be(0);
        empty.DiscoveryStatus.Should().Be("Complete");
        empty.ObservationMethod.Should().Be("EnumerateWebs");
    }

    private async Task<ClassicPageDiscovery> Scope(Guid scan)
    {
        using var db = database.CreateContext();
        return await db.ClassicPageDiscoveries.SingleAsync(row => row.ScanId == scan && row.ScopeType == "SiteCollection");
    }

    private static WebEnumerationResult Enumeration(int count, bool replay = false) => new(
        Enumerable.Range(0, count).Select(index => new EnumeratedWeb { WebUrl = "/web" + index, WebTemplate = "STS#0" }).ToList(), replay);

    private static Web Web(Guid scan, string site, string web, string template, SiteWebStatus status) => new()
    {
        ScanId = scan, SiteUrl = site, WebUrl = web, WebUrlAbsolute = site + web, Template = template, Status = status,
    };
}
