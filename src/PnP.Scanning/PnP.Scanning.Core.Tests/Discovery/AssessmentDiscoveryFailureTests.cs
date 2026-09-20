using CsvHelper;
using CsvHelper.Configuration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Globalization;
using System.Net;
using System.Net.Http;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class AssessmentDiscoveryFailureTests : IClassFixture<ScanContextFixture>
{
    private const string Site = "https://contoso.sharepoint.com/sites/initialization";
    private const string Web = "/";
    private readonly ScanContextFixture database;
    public AssessmentDiscoveryFailureTests(ScanContextFixture database) => this.database = database;
    private AssessmentDiscoveryWriter Writer() => new(database.CreateContext);
    private static Task NoDelay(TimeSpan _, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    private static HttpRequestException ResponseEnded() => new("Error while copying content to a stream.",
        new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely. (ResponseEnded)"));

    [Fact]
    public async Task Exhausted_initialization_preserves_all_116_files_and_exports_explicit_failed_dispositions()
    {
        var scan = Guid.NewGuid();
        var pages = Enumerable.Range(0, 116).Select(i => Page(scan, i)).ToArray();
        await Writer().WriteAsync(pages);
        var attempts = 0;
        var failure = ResponseEnded();
        var operation = () => ClassicAssessmentInitialization.ExecuteAsync<object>(() =>
        {
            attempts++;
            return Task.FromException<object>(failure);
        }, Writer(), scan, Site, Web, CancellationToken.None, delay: NoDelay);
        (await operation.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(failure);
        attempts.Should().Be(3);
        var stored = await Writer().ReadPagesAsync(scan, Site, Web);
        stored.Should().HaveCount(116).And.OnlyContain(row => row.DiscoveryStatus == "Discovered"
            && row.AssessmentStatus == "Failed" && row.ErrorStage == "AssessmentInitialization"
            && row.ErrorCodes.Contains("HttpRequestException") && row.ErrorDetail.Contains("ResponseEnded"));
        stored.Select(row => (row.FileUniqueId, row.Url)).Should().BeEquivalentTo(pages.Select(row => (row.FileUniqueId, row.Url)));
        stored.Should().OnlyContain(row => row.ObservedAtUtc == pages[0].ObservedAtUtc);

        var folder = Path.Combine(Path.GetTempPath(), "native-initialization-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var db = database.CreateContext();
            await ReportManager.ExportClassicReportDataAsync(db, scan, folder, new CsvConfiguration(CultureInfo.InvariantCulture));
            using var csv = new CsvReader(new StreamReader(Path.Combine(folder, "discovery.csv")), CultureInfo.InvariantCulture);
            var exported = csv.GetRecords<ClassicPageDiscovery>().Where(row => row.RowType == "Page").ToArray();
            exported.Should().HaveCount(116).And.OnlyContain(row => row.AssessmentStatus == "Failed" && row.ErrorDetail.Contains("ResponseEnded"));
            exported.Select(row => row.FileUniqueId).Should().BeEquivalentTo(pages.Select(row => row.FileUniqueId));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task Response_body_interruption_retries_at_most_three_attempts_without_stamping_failures_on_success()
    {
        var scan = Guid.NewGuid();
        await Writer().WriteAsync(new[] { Page(scan, 0) });
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var retried = new List<int>();
        var expected = new object();
        var result = await ClassicAssessmentInitialization.ExecuteAsync(() => ++attempts < 3
            ? Task.FromException<object>(ResponseEnded()) : Task.FromResult(expected), Writer(), scan, Site, Web,
            CancellationToken.None, (attempt, _) => retried.Add(attempt), (wait, _) => { delays.Add(wait); return Task.CompletedTask; });
        result.Should().BeSameAs(expected);
        attempts.Should().Be(3);
        retried.Should().Equal(1, 2);
        delays.Should().Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        var row = (await Writer().ReadPagesAsync(scan, Site, Web)).Single();
        row.AssessmentStatus.Should().BeNull("the page loop, not context initialization, determines success/applicability");
        row.ErrorCodes.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Permission_errors_are_finalized_but_not_retried(HttpStatusCode status)
    {
        var scan = Guid.NewGuid();
        await Writer().WriteAsync(new[] { Page(scan, 0) });
        var attempts = 0;
        var operation = () => ClassicAssessmentInitialization.ExecuteAsync<object>(() =>
        {
            attempts++;
            return Task.FromException<object>(new HttpRequestException("HTTP " + (int)status, null, status));
        }, Writer(), scan, Site, Web, CancellationToken.None, delay: (_, _) => throw new InvalidOperationException("must not retry"));
        await operation.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(1);
        (await Writer().ReadPagesAsync(scan, Site, Web)).Single().AssessmentStatus.Should().Be("Failed");
    }

    [Fact]
    public async Task Genuine_cancellation_before_initialization_preserves_resumable_rows()
    {
        var scan = Guid.NewGuid();
        await Writer().WriteAsync(new[] { Page(scan, 0) });
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var attempts = 0;
        var operation = () => ClassicAssessmentInitialization.ExecuteAsync(() => { attempts++; return Task.FromResult(new object()); },
            Writer(), scan, Site, Web, cancel.Token, delay: NoDelay);
        await operation.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(0);
        (await Writer().ReadPagesAsync(scan, Site, Web)).Single().AssessmentStatus.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_during_backoff_stops_retry_without_marking_pages_failed()
    {
        var scan = Guid.NewGuid();
        await Writer().WriteAsync(new[] { Page(scan, 0) });
        using var cancel = new CancellationTokenSource();
        var attempts = 0;
        var operation = () => ClassicAssessmentInitialization.ExecuteAsync<object>(() =>
        { attempts++; return Task.FromException<object>(ResponseEnded()); }, Writer(), scan, Site, Web, cancel.Token,
            delay: (_, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await operation.Should().ThrowAsync<OperationCanceledException>();
        attempts.Should().Be(1);
        (await Writer().ReadPagesAsync(scan, Site, Web)).Single().AssessmentStatus.Should().BeNull();
    }

    [Fact]
    public async Task Non_transient_initialization_failure_is_not_retried()
    {
        var scan = Guid.NewGuid();
        await Writer().WriteAsync(new[] { Page(scan, 0) });
        var attempts = 0;
        var operation = () => ClassicAssessmentInitialization.ExecuteAsync<object>(() =>
        { attempts++; throw new InvalidOperationException("invalid metadata shape"); }, Writer(), scan, Site, Web,
            CancellationToken.None, delay: NoDelay);
        await operation.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1);
        (await Writer().ReadPagesAsync(scan, Site, Web)).Single().AssessmentStatus.Should().Be("Failed");
    }

    [Fact]
    public async Task Failure_finalization_is_idempotent_and_scoped_and_preserves_terminal_rows_and_scope_evidence()
    {
        var scan = Guid.NewGuid();
        var unfinished = Page(scan, 0);
        unfinished.ErrorStage = "DiscoveryMetadata"; unfinished.ErrorCodes = "earlier_warning";
        var emptyStatus = Page(scan, 1); emptyStatus.AssessmentStatus = "";
        var terminal = new[] { "Complete", "Failed", "NotApplicable", "NotSelected" }.Select((status, i) =>
        { var row = Page(scan, i + 2); row.AssessmentStatus = status; row.ErrorDetail = "retained"; return row; }).ToArray();
        var anotherWeb = Page(scan, 7); anotherWeb.WebUrl = "/other";
        var anotherSite = Page(scan, 8); anotherSite.SiteUrl = Site + "-other";
        var anotherScan = Page(Guid.NewGuid(), 9);
        var scope = new ClassicPageDiscovery { ScanId = scan, SiteUrl = Site, WebUrl = Web, RecordKey = "scope:denied", RowType = "Scope", DiscoveryStatus = "Denied" };
        await Writer().WriteAsync(new[] { unfinished, emptyStatus, anotherWeb, anotherSite, anotherScan, scope }.Concat(terminal));
        (await Writer().FailUnassessedPagesAsync(scan, Site, Web, ResponseEnded(), "WebScan")).Should().Be(2);
        (await Writer().FailUnassessedPagesAsync(scan, Site, Web, new IOException("later"), "LaterStage")).Should().Be(0);
        using var db = database.CreateContext();
        var all = await db.ClassicPageDiscoveries.Where(row => row.ScanId == scan).ToListAsync();
        all.Single(row => row.RecordKey == unfinished.RecordKey).ErrorCodes.Should().Contain("earlier_warning").And.Contain("HttpRequestException");
        foreach (var row in terminal) all.Single(stored => stored.RecordKey == row.RecordKey).Should().BeEquivalentTo(row);
        all.Single(row => row.RecordKey == anotherWeb.RecordKey).AssessmentStatus.Should().BeNull();
        all.Single(row => row.RecordKey == anotherSite.RecordKey).AssessmentStatus.Should().BeNull();
        all.Single(row => row.RecordKey == scope.RecordKey).Should().BeEquivalentTo(scope);
        (await db.ClassicPageDiscoveries.SingleAsync(row => row.ScanId == anotherScan.ScanId)).AssessmentStatus.Should().BeNull();
    }

    [Fact]
    public async Task Failure_to_persist_finalization_is_not_reported_as_success()
    {
        var writer = new AssessmentDiscoveryWriter(() => throw new IOException("database unavailable"));
        var operation = () => ClassicAssessmentInitialization.ExecuteAsync<object>(() => throw new InvalidOperationException("bad response"),
            writer, Guid.NewGuid(), Site, Web, CancellationToken.None, delay: NoDelay);
        await operation.Should().ThrowAsync<IOException>().WithMessage("database unavailable");
    }

    private static ClassicPageDiscovery Page(Guid scan, int index) => new()
    {
        ScanId = scan, SiteUrl = Site, WebUrl = Web, RecordKey = "page:" + index,
        RowType = "Page", ScopeType = "File", Url = "/sites/initialization/Pages/" + index + ".aspx",
        FileName = index + ".aspx", SiteCollectionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        WebId = Guid.Parse("22222222-2222-2222-2222-222222222222"), FileUniqueId = Guid.NewGuid(),
        DiscoveryStatus = "Discovered", ObservedAtUtc = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
    };
}
