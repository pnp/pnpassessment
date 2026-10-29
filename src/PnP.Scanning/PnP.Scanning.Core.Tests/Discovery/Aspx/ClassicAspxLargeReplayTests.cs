using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Storage;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.Aspx;

public sealed class ClassicAspxLargeReplayTests
{
    /// <summary>
    /// Streams ten thousand distinct items through bounded pages and publication batches while resolving and reading their shared renderer once.
    /// </summary>
    [Fact]
    public async Task TenThousandOnDemandItemsUseOneRendererBoundedBatchesAndConstantSourceReads()
    {
        await using var scenario = new ClassicAspxStreamingFixture();
        await scenario.InitializeAsync();
        var source = scenario.Client("large-first");
        await scenario.RunAsync(source);
        using var db = scenario.Open();
        Assert.Equal(1, await db.ClassicAspxInventories.CountAsync());
        Assert.Equal(10000, await db.ClassicAspxItemBindings.CountAsync());
        Assert.Equal(10000, await db.ClassicAspxItemBindings.Select(x => x.ItemId).Distinct().CountAsync());
        Assert.Equal(1, await db.ClassicAspxItemBindings.MinAsync(x => x.ItemId));
        Assert.Equal(10000, await db.ClassicAspxItemBindings.MaxAsync(x => x.ItemId));
        Assert.InRange(scenario.LargestPublishedBatch, 1, 128);
        Assert.True(scenario.PublishedBatches > 1);
        Assert.Equal(50, source.ItemPageRequests);
        Assert.Equal(10000, source.GeneratedItems);
        Assert.Equal(200, source.LargestGeneratedPage);
        Assert.Equal(1, source.ResolveCalls);
        Assert.Equal(1, source.SourceCalls);
        Assert.DoesNotContain(await db.ClassicPageDiscoveries.Where(x => x.RowType == "Scope").ToArrayAsync(),
            row => row.DiscoveryStatus is "Denied" or "Failed" or "Truncated" or "Unknown");
    }

    /// <summary>
    /// Replays denied, successful, and truncated acquisitions in one scan, preserving historical gaps and merging renderer reasons without duplicate pairs.
    /// </summary>
    [Fact]
    public async Task SameScanReplayUnionsSourcesAndRetainsDeniedTruncatedRawScopesAndObservedPairs()
    {
        await using var scenario = new ClassicAspxStreamingFixture();
        await scenario.InitializeAsync();
        var denied = scenario.Client("partial-denied", denySecondItemPage: true);
        await scenario.RunAsync(denied);
        string failedScopeKey;
        using (var db = scenario.Open())
        {
            Assert.Equal(200, await db.ClassicAspxItemBindings.CountAsync());
            failedScopeKey = (await db.ClassicPageDiscoveries.SingleAsync(x => x.RowType == "Scope" &&
                x.DiscoveryStatus == "Denied")).RecordKey;
        }
        var successful = scenario.Client("replay-list-form");
        await scenario.RunAsync(successful);
        using (var db = scenario.Open())
        {
            Assert.Equal(1, await db.ClassicAspxInventories.CountAsync());
            Assert.Equal(10000, await db.ClassicAspxItemBindings.CountAsync());
            var retained = await db.ClassicPageDiscoveries.SingleAsync(x => x.RecordKey == failedScopeKey);
            Assert.Equal("Denied", retained.DiscoveryStatus);
            Assert.NotNull(retained.ErrorCodes);
        }
        var truncated = scenario.Client("partial-truncated", overrideForm: true, truncatedSource: true);
        await scenario.RunAsync(truncated, singlePageCap: true);
        using (var db = scenario.Open())
        {
            Assert.Equal(10000, await db.ClassicAspxItemBindings.CountAsync());
            Assert.Equal("Truncated", (await db.ClassicPageDiscoveries.SingleAsync(x => x.RecordKey == failedScopeKey)).DiscoveryStatus);
            var file = await db.ClassicAspxInventories.SingleAsync();
            Assert.Equal("Example.SharedRenderer", file.HandlerEffectiveType);
            Assert.Contains("synthetic_source_truncated", file.HandlerErrorCodes);
        }
        var replay = scenario.Client("replay-content-type", overrideForm: true);
        await scenario.RunAsync(replay);
        using (var db = scenario.Open())
        {
            Assert.Equal(1, await db.ClassicAspxInventories.CountAsync());
            Assert.Equal(10000, await db.ClassicAspxItemBindings.CountAsync());
            Assert.Equal(10000, await db.ClassicAspxItemBindings.Select(x => x.ItemId).Distinct().CountAsync());
            Assert.Equal("Truncated", (await db.ClassicPageDiscoveries.SingleAsync(x => x.RecordKey == failedScopeKey)).DiscoveryStatus);
            var pairs = await db.ClassicAspxItemBindings.AsNoTracking().ToArrayAsync();
            Assert.All(pairs, pair =>
            {
                Assert.Equal(ClassicAspxBindingKinds.ListFormRenderer | ClassicAspxBindingKinds.ContentTypeRenderer, pair.BindingKinds);
                using var provenance = JsonDocument.Parse(pair.BindingProvenanceJson);
                var sources = provenance.RootElement.EnumerateArray().Select(e => e.GetProperty("source").GetString()).ToArray();
                Assert.Contains("List.DefaultDisplayFormUrl", sources);
                Assert.Contains("MostSpecificListContentType.DisplayFormUrl", sources);
            });
            using var fileSources = JsonDocument.Parse((await db.ClassicAspxInventories.SingleAsync()).ProvenanceJson);
            Assert.True(fileSources.RootElement.GetArrayLength() >= 3);
        }
        foreach (var attempt in new[] { denied, successful, truncated, replay })
        {
            Assert.Equal(1, attempt.ResolveCalls); Assert.Equal(1, attempt.SourceCalls);
            Assert.InRange(attempt.LargestGeneratedPage, 1, 200);
        }
        Assert.InRange(scenario.LargestPublishedBatch, 1, 128);
    }
}
