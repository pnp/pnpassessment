using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PnP.Scanning.Core.Tests.Scanners.Pages;

/// <summary>
/// Pins the cost of the post-scan prefetch in
/// <see cref="PublishingLayoutStatusReader.EnrichSiteAsync"/> to the number of distinct layouts
/// rather than to the number of publishing pages.
///
/// Every page in these tests shares a single layout, so the work the enrichment performs is
/// constant. Anything that still grows with the page count is prefetch overhead.
/// </summary>
public class PublishingLayoutPrefetchScaleTests
{
    private const string SiteUrl = "https://contoso.sharepoint.com/sites/a";
    private const string LayoutUrl = "/sites/a/_catalogs/masterpage/Basic.aspx";

    private readonly ITestOutputHelper output;

    public PublishingLayoutPrefetchScaleTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(5000)]
    public async Task Prefetch_tracks_only_the_shared_layout_whatever_the_page_count(int pageCount)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var commands = new List<string>();
        using var db = CreateContext(connection, commands);
        await db.Database.MigrateAsync();
        var scan = await SeedAsync(db, pageCount);

        db.ChangeTracker.Clear();
        commands.Clear();

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new InvalidOperationException("The shared layout is in discovery; no fallback is expected."),
            (error, _) => throw new InvalidOperationException("Unexpected enrichment failure", error));

        var tracked = db.ChangeTracker.Entries<ClassicPageDiscovery>().Count();
        var prefetch = commands.Single(text => text.Contains("ClassicPageDiscoveries", StringComparison.Ordinal));

        output.WriteLine($"pages={pageCount,6}  trackedDiscoveryRows={tracked,6}  " +
            $"prefetchParameterBytes={ParameterPayloadLength(prefetch),8}");

        // One shared layout, matched by both its server-relative and absolute spelling.
        tracked.Should().Be(1);

        db.ChangeTracker.Clear();
        var pages = await db.ClassicPages.Where(page => page.ScanId == scan).ToListAsync();
        pages.Should().HaveCount(pageCount)
            .And.OnlyContain(page => page.LayoutCustomizedPageStatus == "Uncustomized");
    }

    [Fact]
    public async Task Prefetch_cost_is_flat_across_page_counts()
    {
        var measurements = new List<(int Pages, int Tracked, int ParameterBytes)>();
        foreach (var pageCount in new[] { 100, 1000, 5000 })
        {
            using var connection = new SqliteConnection("Filename=:memory:");
            await connection.OpenAsync();
            var commands = new List<string>();
            using var db = CreateContext(connection, commands);
            await db.Database.MigrateAsync();
            var scan = await SeedAsync(db, pageCount);
            db.ChangeTracker.Clear();
            commands.Clear();

            await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
                _ => throw new InvalidOperationException("No fallback expected"),
                (error, _) => throw new InvalidOperationException("Unexpected enrichment failure", error));

            var prefetch = commands.Single(text => text.Contains("ClassicPageDiscoveries", StringComparison.Ordinal));
            measurements.Add((pageCount, db.ChangeTracker.Entries<ClassicPageDiscovery>().Count(),
                ParameterPayloadLength(prefetch)));
        }

        foreach (var (pages, tracked, bytes) in measurements)
            output.WriteLine($"pages={pages,6}  trackedDiscoveryRows={tracked,6}  prefetchParameterBytes={bytes,8}");

        measurements.Select(value => value.Tracked).Distinct().Should().ContainSingle();
        measurements.Select(value => value.ParameterBytes).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task A_failing_page_still_records_the_error_on_its_own_discovery_row()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var db = CreateContext(connection, new List<string>());
        await db.Database.MigrateAsync();
        var scan = await SeedAsync(db, 50);
        db.ChangeTracker.Clear();

        var failures = new List<string>();
        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new UnauthorizedAccessException("Layout denied"),
            (_, page) => failures.Add(page.PageUrl));

        // Discovery holds the layout, so force the fallback by removing it first.
        failures.Should().BeEmpty("discovery satisfied every page");

        db.ChangeTracker.Clear();
        var layoutRow = await db.ClassicPageDiscoveries.SingleAsync(row => row.RecordKey == "layout");
        db.ClassicPageDiscoveries.Remove(layoutRow);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new UnauthorizedAccessException("Layout denied"),
            (_, page) => failures.Add(page.PageUrl));

        failures.Should().HaveCount(50, "every page shares the unreadable layout");
        db.ChangeTracker.Clear();
        var rows = await db.ClassicPageDiscoveries
            .Where(row => row.ScanId == scan && row.ErrorStage != null).ToListAsync();
        rows.Should().HaveCount(50)
            .And.OnlyContain(row => row.ErrorStage == "PageLayoutMetadata" &&
                row.ErrorDetail.Contains("Layout denied"));
    }

    private static ScanContext CreateContext(SqliteConnection connection, List<string> commands)
    {
        var options = new DbContextOptionsBuilder<ScanContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(ScanContext).Assembly.FullName))
            .EnableSensitiveDataLogging()
            .LogTo(commands.Add, new[] { RelationalEventId.CommandExecuting })
            .Options;
        return new ScanContext(options);
    }

    private static async Task<Guid> SeedAsync(ScanContext db, int pageCount)
    {
        var scan = Guid.NewGuid();
        for (var index = 0; index < pageCount; index++)
        {
            var pageUrl = $"/sites/a/Pages/page-{index}.aspx";
            db.ClassicPages.Add(new ClassicPage
            {
                ScanId = scan,
                SiteUrl = SiteUrl,
                WebUrl = "/",
                PageUrl = pageUrl,
                PageType = PageScanComponent.PublishingPage,
                LayoutUrl = LayoutUrl,
                AssessmentStatus = "Complete",
            });

            // A real scan records a discovery row for every page it enumerated.
            db.ClassicPageDiscoveries.Add(new ClassicPageDiscovery
            {
                ScanId = scan,
                SiteUrl = SiteUrl,
                WebUrl = "/",
                RecordKey = "page:" + index,
                RowType = "Page",
                Url = pageUrl,
                DiscoveryStatus = "Discovered",
                ObservedAtUtc = DateTime.UtcNow,
            });
        }

        db.ClassicPageDiscoveries.Add(new ClassicPageDiscovery
        {
            ScanId = scan,
            SiteUrl = SiteUrl,
            WebUrl = "/",
            RecordKey = "layout",
            RowType = "Page",
            Url = LayoutUrl,
            CustomizedPageStatus = "Uncustomized",
            DiscoveryStatus = "Discovered",
            ObservedAtUtc = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return scan;
    }

    // EF Core 8 sends a primitive collection as a single JSON parameter consumed by json_each.
    private static int ParameterPayloadLength(string loggedCommand)
    {
        var start = loggedCommand.IndexOf('[');
        var end = loggedCommand.LastIndexOf(']');
        return start >= 0 && end > start ? end - start + 1 : loggedCommand.Length;
    }
}
