using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PnP.Scanning.Core.Tests.Scanners.Pages;

public class PublishingLayoutFailureBatchTests
{
    private const string SiteUrl = "https://contoso.sharepoint.com/sites/a";
    private const string LayoutUrl = "/sites/a/_catalogs/masterpage/Basic.aspx";
    private const int MaximumKeysPerQuery = 256;
    private readonly ITestOutputHelper output;

    public PublishingLayoutFailureBatchTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(5000)]
    public async Task Shared_layout_failure_scans_identities_once_and_fetches_bounded_indexed_batches(int pageCount)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var reads = new DiscoveryReads();
        using var db = CreateContext(connection, reads);
        await db.Database.MigrateAsync();
        var scan = await SeedAsync(db, pageCount);
        db.ChangeTracker.Clear();
        reads.Commands.Clear();
        var requests = 0;
        var failures = 0;

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl, _ =>
        {
            requests++;
            throw new UnauthorizedAccessException("Shared layout denied");
        }, (_, _) => failures++);

        requests.Should().Be(1, "all pages share the same cached terminal failure");
        failures.Should().Be(pageCount);
        var identityReads = reads.Commands.Where(command => !command.Sql.Contains("json_each", StringComparison.Ordinal)).ToArray();
        identityReads.Length.Should().Be(1, "failure identities must be scanned once, not once per page");
        identityReads[0].Sql.Should().NotContain("COLLATE");
        var batches = reads.Commands.Where(command => command.Sql.Contains("\"RecordKey\" IN", StringComparison.Ordinal)).ToArray();
        batches.Should().HaveCount((pageCount + MaximumKeysPerQuery - 1) / MaximumKeysPerQuery);
        foreach (var batch in batches)
        {
            var keys = batch.Parameters.Where(parameter => parameter.Value is string text && text.StartsWith('['))
                .Select(parameter => JsonSerializer.Deserialize<string[]>((string)parameter.Value)).Single();
            keys.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(MaximumKeysPerQuery);
            batch.Sql.Should().NotContain("COLLATE");
        }

        // Check the actual EF-generated query against this product's migrated SQLite schema.
        using (var explain = connection.CreateCommand())
        {
            explain.CommandText = "EXPLAIN QUERY PLAN " + batches[0].Sql;
            foreach (var parameter in batches[0].Parameters)
                explain.Parameters.AddWithValue(parameter.Name, parameter.Value);
            var plan = new List<string>();
            using var result = await explain.ExecuteReaderAsync();
            while (await result.ReadAsync()) plan.Add(result.GetString(3));
            plan.Should().Contain(detail => detail.Contains("SEARCH", StringComparison.Ordinal) &&
                detail.Contains("ScanId=?", StringComparison.Ordinal) && detail.Contains("RecordKey=?", StringComparison.Ordinal));
        }

        output.WriteLine($"pages={pageCount} identityScans={identityReads.Length} keyBatches={batches.Length}");
        db.ChangeTracker.Clear();
        var rows = await db.ClassicPageDiscoveries.Where(row => row.ScanId == scan).ToListAsync();
        rows.Should().HaveCount(pageCount).And.OnlyContain(row => row.ErrorStage.Contains("PageLayoutMetadata") &&
            row.ErrorStage.Contains("ExistingStage") && row.ErrorDetail.Contains("Shared layout denied") &&
            row.ErrorDetail.Contains("Existing detail") && row.AssessmentStatus == "Complete");
        var pages = await db.ClassicPages.Where(page => page.ScanId == scan).ToListAsync();
        pages.Should().HaveCount(pageCount).And.OnlyContain(page => page.LayoutCustomizedPageStatus == null &&
            page.AssessmentStatus == "Complete" && page.WebPartCount == 3 && page.MappingPercentage == 50 && page.RemediationCode == "CP3");
    }

    [Fact]
    public async Task Failure_batches_track_only_matching_rows_and_preserve_scan_site_web_and_row_type_scope()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var db = CreateContext(connection, new DiscoveryReads());
        await db.Database.MigrateAsync();
        var scan = await SeedAsync(db, 1);
        var url = PageUrl(0);
        var duplicate = Discovery(scan, "duplicate", url);
        var otherWeb = Discovery(scan, "other-web", url);
        otherWeb.WebUrl = "/other";
        var otherSite = Discovery(scan, "other-site", url);
        otherSite.SiteUrl = SiteUrl + "-other";
        var otherScan = Discovery(Guid.NewGuid(), "other-scan", url);
        var scope = Discovery(scan, "scope", url);
        scope.RowType = "Scope";
        db.ClassicPageDiscoveries.AddRange(duplicate, otherWeb, otherSite, otherScan, scope);
        db.ClassicPageDiscoveries.AddRange(Enumerable.Range(0, 2000)
            .Select(index => Discovery(scan, "healthy:" + index, "/sites/a/unrelated/" + index + ".aspx")));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new UnauthorizedAccessException("Layout denied"), (_, _) => { });

        db.ChangeTracker.Entries<ClassicPageDiscovery>().Select(entry => entry.Entity.RecordKey)
            .Should().BeEquivalentTo(new[] { "page:000000", "duplicate" });
        db.ChangeTracker.Clear();
        var rows = await db.ClassicPageDiscoveries.ToListAsync();
        rows.Where(row => row.ErrorStage.Contains("PageLayoutMetadata")).Select(row => row.RecordKey)
            .Should().BeEquivalentTo(new[] { "page:000000", "duplicate" });
    }

    [Theory]
    [InlineData("/WEB", "/web")]
    [InlineData("/Équipe", "/équipe")]
    [InlineData("/中文", "/中文")]
    public async Task Failure_identity_matching_uses_ordinal_ignore_case(string pageWeb, string discoveryWeb)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var db = CreateContext(connection, new DiscoveryReads());
        await db.Database.MigrateAsync();
        var scan = Guid.NewGuid();
        var page = Page(scan, 0);
        page.WebUrl = pageWeb;
        var row = Discovery(scan, "page", page.PageUrl);
        row.WebUrl = discoveryWeb;
        db.ClassicPages.Add(page);
        db.ClassicPageDiscoveries.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new UnauthorizedAccessException("Layout denied"), (_, _) => { });

        db.ChangeTracker.Clear();
        (await db.ClassicPageDiscoveries.SingleAsync()).ErrorStage.Should().Contain("PageLayoutMetadata");
    }

    [Fact]
    public async Task Multiple_failures_for_one_identity_are_all_retained()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var db = CreateContext(connection, new DiscoveryReads());
        await db.Database.MigrateAsync();
        var scan = await SeedAsync(db, 1);
        var second = Page(scan, 1);
        second.PageUrl = PageUrl(0).ToUpperInvariant();
        second.LayoutUrl = "/sites/a/_catalogs/masterpage/Other.aspx";
        db.ClassicPages.Add(second);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            url => throw new UnauthorizedAccessException(url == LayoutUrl ? "First failure" : "Second failure"), (_, _) => { });

        db.ChangeTracker.Clear();
        var saved = await db.ClassicPageDiscoveries.SingleAsync();
        saved.ErrorDetail.Should().Contain("First failure").And.Contain("Second failure").And.Contain("Existing detail");
    }

    [Fact]
    public async Task Failure_batches_reuse_tracked_layout_rows_without_losing_pending_conflict_errors()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var db = CreateContext(connection, new DiscoveryReads());
        await db.Database.MigrateAsync();
        var scan = Guid.NewGuid();
        var page = Page(scan, 0);
        page.PageUrl = LayoutUrl;
        var previous = Discovery(scan, "previous-layout", LayoutUrl);
        previous.CustomizedPageStatus = "Customized";
        previous.ObservedAtUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var latest = Discovery(scan, "latest-layout", LayoutUrl);
        latest.CustomizedPageStatus = "99";
        latest.ObservedAtUtc = previous.ObservedAtUtc.AddMinutes(1);
        db.ClassicPages.Add(page);
        db.ClassicPageDiscoveries.AddRange(previous, latest);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var failures = 0;

        await PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new InvalidOperationException("Discovery must satisfy the layout read"), (_, _) => failures++);

        failures.Should().Be(1, "the latest discovery value is not a recognized customization status");
        db.ChangeTracker.Clear();
        var saved = await db.ClassicPageDiscoveries.FindAsync(scan, "latest-layout");
        saved.ErrorStage.Should().Contain("DiscoveryMetadata").And.Contain("PageLayoutMetadata").And.Contain("ExistingStage");
        saved.ErrorDetail.Should().Contain("changed customization status").And.Contain("Existing detail");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_lookup_database_errors_propagate_without_partially_saving_errors(bool duringKeyBatch)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var reads = new DiscoveryReads();
        using var db = CreateContext(connection, reads);
        await db.Database.MigrateAsync();
        var scan = await SeedAsync(db, 300);
        db.ChangeTracker.Clear();
        var keyBatches = 0;
        reads.OnRead = command =>
        {
            if (duringKeyBatch ? command.CommandText.Contains("\"RecordKey\" IN", StringComparison.Ordinal) && ++keyBatches == 2
                : !command.CommandText.Contains("json_each", StringComparison.Ordinal))
                throw new InvalidOperationException("Failure metadata store unavailable");
        };

        var enrich = () => PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new UnauthorizedAccessException("Layout denied"), (_, _) => { });
        await enrich.Should().ThrowAsync<InvalidOperationException>().WithMessage("Failure metadata store unavailable");

        db.ChangeTracker.Clear();
        reads.OnRead = null;
        (await db.ClassicPageDiscoveries.ToListAsync()).Should()
            .OnlyContain(row => row.ErrorStage == "ExistingStage" && row.ErrorDetail == "Existing detail");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_failure_lookup_propagates_without_persisting_errors(bool duringKeyBatch)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var reads = new DiscoveryReads();
        using var db = CreateContext(connection, reads);
        await db.Database.MigrateAsync();
        var scan = await SeedAsync(db, 2);
        db.ChangeTracker.Clear();
        reads.OnRead = command =>
        {
            if (duringKeyBatch ? command.CommandText.Contains("\"RecordKey\" IN", StringComparison.Ordinal)
                : !command.CommandText.Contains("json_each", StringComparison.Ordinal)) cancellation.Cancel();
        };

        var enrich = () => PublishingLayoutStatusReader.EnrichSiteAsync(db, scan, SiteUrl,
            _ => throw new UnauthorizedAccessException("Layout denied"), (_, _) => { }, cancellation.Token);
        await enrich.Should().ThrowAsync<OperationCanceledException>();

        db.ChangeTracker.Clear();
        reads.OnRead = null;
        (await db.ClassicPageDiscoveries.ToListAsync()).Should()
            .OnlyContain(row => row.ErrorStage == "ExistingStage" && row.ErrorDetail == "Existing detail");
    }

    private static ScanContext CreateContext(SqliteConnection connection, DiscoveryReads reads) => new(
        new DbContextOptionsBuilder<ScanContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly(typeof(ScanContext).Assembly.FullName))
            .AddInterceptors(reads).Options);

    private static async Task<Guid> SeedAsync(ScanContext db, int count)
    {
        var scan = Guid.NewGuid();
        for (var index = 0; index < count; index++)
        {
            db.ClassicPages.Add(Page(scan, index));
            db.ClassicPageDiscoveries.Add(Discovery(scan, $"page:{index:D6}", PageUrl(index)));
        }
        await db.SaveChangesAsync();
        return scan;
    }

    private static string PageUrl(int index) => $"/sites/a/Pages/page-{index}.aspx";

    private static ClassicPage Page(Guid scan, int index) => new()
    {
        ScanId = scan, SiteUrl = SiteUrl, WebUrl = "/", PageUrl = PageUrl(index),
        PageType = PageScanComponent.PublishingPage, LayoutUrl = LayoutUrl, AssessmentStatus = "Complete",
        WebPartCount = 3, MappingPercentage = 50, RemediationCode = "CP3",
    };

    private static ClassicPageDiscovery Discovery(Guid scan, string key, string url) => new()
    {
        ScanId = scan, SiteUrl = SiteUrl, WebUrl = "/", RecordKey = key, RowType = "Page", Url = url,
        DiscoveryStatus = "Discovered", AssessmentStatus = "Complete", ErrorStage = "ExistingStage",
        ErrorCodes = "ExistingCode", ErrorDetail = "Existing detail",
    };

    private sealed record RecordedCommand(string Sql, (string Name, object Value)[] Parameters);

    private sealed class DiscoveryReads : DbCommandInterceptor
    {
        internal List<RecordedCommand> Commands { get; } = new();
        internal Action<DbCommand> OnRead { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal) &&
                command.CommandText.Contains("FROM \"ClassicPageDiscoveries\"", StringComparison.Ordinal))
            {
                Commands.Add(new RecordedCommand(command.CommandText, command.Parameters.Cast<DbParameter>()
                    .Select(parameter => (parameter.ParameterName, parameter.Value)).ToArray()));
                OnRead?.Invoke(command);
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
