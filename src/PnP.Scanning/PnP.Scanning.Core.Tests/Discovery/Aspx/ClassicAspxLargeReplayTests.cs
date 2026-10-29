using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.Aspx;

public sealed class ClassicAspxLargeReplayTests
{
    [Fact]
    public async Task Ten_thousand_item_list_is_never_enumerated_by_native_acquisition()
    {
        await using var scenario = new ClassicAspxStreamingFixture();
        await scenario.InitializeAsync();
        var source = scenario.Client("large-first");
        await scenario.RunAsync(source);

        using var db = scenario.Open();
        Assert.Equal(0, await db.ClassicAspxItemBindings.CountAsync());
        Assert.Equal(0, await db.ClassicAspxInventories.CountAsync());
        var marker = await db.ClassicPageDiscoveries.SingleAsync(x =>
            x.RowType == "Scope" && x.ScopeType == "ItemEnumerationPolicy");
        Assert.Equal("PolicyExcluded", marker.DiscoveryStatus);
        using var details = JsonDocument.Parse(marker.EvidenceJson);
        Assert.Equal("NotCollected", details.RootElement.GetProperty("itemPages").GetString());
        Assert.Equal("NotCollected", details.RootElement.GetProperty("itemBindings").GetString());
        Assert.Equal(0, source.TotalPageRequests);
        Assert.Equal(0, source.ItemPageRequests);
        Assert.Equal(0, source.GeneratedItems);
        Assert.Equal(0, source.ResolveCalls);
        Assert.Equal(0, source.SourceCalls);
        Assert.InRange(scenario.LargestPublishedBatch, 1, 128);
    }

    [Fact]
    public async Task Replay_keeps_one_policy_marker_without_reading_items()
    {
        await using var scenario = new ClassicAspxStreamingFixture();
        await scenario.InitializeAsync();
        var first = scenario.Client("first");
        var replay = scenario.Client("replay", overrideForm: true, denySecondItemPage: true);
        await scenario.RunAsync(first);
        await scenario.RunAsync(replay);

        using var db = scenario.Open();
        Assert.Equal(1, await db.ClassicPageDiscoveries.CountAsync(x =>
            x.RowType == "Scope" && x.ScopeType == "ItemEnumerationPolicy"));
        Assert.Equal(0, await db.ClassicAspxItemBindings.CountAsync());
        Assert.Equal(0, first.TotalPageRequests);
        Assert.Equal(0, replay.TotalPageRequests);
        Assert.Equal(0, first.GeneratedItems);
        Assert.Equal(0, replay.GeneratedItems);
    }
}
