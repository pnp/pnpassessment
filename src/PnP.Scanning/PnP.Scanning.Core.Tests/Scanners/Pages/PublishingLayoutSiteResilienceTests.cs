using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages;

/// <summary>
/// Covers the resilience policy around the per-site publishing layout enrichment loop.
///
/// Layout status is an enrichment applied during post-scanning, ahead of the discovery
/// finalization that produces the coverage verdict. A site collection that has become
/// unreachable since the scan ran must therefore cost only its own layout status, not the
/// remaining site collections and not the assessment's verdict. Cancellation is the one
/// condition that still stops the loop, because it means the assessment is being terminated.
/// </summary>
public class PublishingLayoutSiteResilienceTests
{
    private static readonly string[] Sites =
    {
        "https://contoso.sharepoint.com/sites/a",
        "https://contoso.sharepoint.com/sites/b",
        "https://contoso.sharepoint.com/sites/c",
    };

    [Fact]
    public async Task Every_site_is_visited_when_none_fail()
    {
        var visited = new List<string>();

        var failed = await ClassicScanner.EnrichSitesAsync(Sites,
            siteUrl => { visited.Add(siteUrl); return Task.CompletedTask; },
            (_, _) => throw new InvalidOperationException("No site should fail"));

        failed.Should().Be(0);
        visited.Should().Equal(Sites);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_failing_site_is_reported_and_the_remaining_sites_still_run(int failingIndex)
    {
        var visited = new List<string>();
        var reported = new List<string>();

        var failed = await ClassicScanner.EnrichSitesAsync(Sites,
            siteUrl =>
            {
                visited.Add(siteUrl);
                return siteUrl == Sites[failingIndex]
                    ? Task.FromException(new UnauthorizedAccessException("Site is gone"))
                    : Task.CompletedTask;
            },
            (error, siteUrl) =>
            {
                error.Should().BeOfType<UnauthorizedAccessException>();
                reported.Add(siteUrl);
            });

        failed.Should().Be(1);
        reported.Should().Equal(Sites[failingIndex]);
        visited.Should().Equal(Sites, "a failure must not shorten the loop");
    }

    [Fact]
    public async Task Every_site_failing_is_counted_without_throwing()
    {
        var reported = new List<string>();

        var failed = await ClassicScanner.EnrichSitesAsync(Sites,
            _ => Task.FromException(new InvalidOperationException("boom")),
            (_, siteUrl) => reported.Add(siteUrl));

        failed.Should().Be(Sites.Length);
        reported.Should().Equal(Sites);
    }

    [Fact]
    public async Task Cancellation_stops_the_loop_and_is_not_recorded_as_a_site_failure()
    {
        using var cancellation = new CancellationTokenSource();
        var visited = new List<string>();

        var run = () => ClassicScanner.EnrichSitesAsync(Sites,
            siteUrl =>
            {
                visited.Add(siteUrl);
                cancellation.Cancel();
                return Task.FromException(new OperationCanceledException(cancellation.Token));
            },
            (_, _) => throw new InvalidOperationException("Cancellation is not a site failure"),
            cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        visited.Should().ContainSingle("cancellation must stop the loop immediately");
    }

    [Fact]
    public async Task A_token_cancelled_before_the_loop_visits_no_site()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var run = () => ClassicScanner.EnrichSitesAsync(Sites,
            _ => throw new InvalidOperationException("No site should be visited"),
            (_, _) => throw new InvalidOperationException("No failure should be recorded"),
            cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task An_empty_site_list_is_a_no_op()
    {
        var failed = await ClassicScanner.EnrichSitesAsync(Array.Empty<string>(),
            _ => throw new InvalidOperationException("Nothing to enrich"),
            (_, _) => throw new InvalidOperationException("Nothing to report"));

        failed.Should().Be(0);
    }
}
