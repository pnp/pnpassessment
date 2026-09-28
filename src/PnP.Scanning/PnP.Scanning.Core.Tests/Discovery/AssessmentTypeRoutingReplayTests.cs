using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed partial class AssessmentPageMetadataReplayTests
{
    [Theory]
    [InlineData("Root", null)]
    [InlineData("Direct", "0x010109")]
    [InlineData("Indirect", PublishingContentType)]
    public async Task Confirmed_family_blocks_direct_metadata_loading_before_any_list_request(string type, string contentType)
    {
        var fixture = new MetadataFixture(Guid.NewGuid(), "Custom/handler.aspx", new()
        {
            ["ContentTypeId"] = PublishingContentType,
            ["WikiField"] = "Must not be processed",
        });
        fixture.Row.ContentTypeId = contentType;
        fixture.Row.PageType = "PublishingPage";
        await LayoutRoutingEvidence.InspectAsync(fixture.Row, type);
        (await PageScanComponent.LoadPhysicalPageAsync(fixture.List, fixture.Row, null, true, ruleVersion: 1)).Should().BeNull();
        fixture.ItemRequests.Should().Be(0);
        fixture.Row.PageType.Should().BeNull();
        fixture.Row.AssessmentStatus.Should().Be("ExcludedAsset");
    }

    [Theory]
    [InlineData("Outside", false)]
    [InlineData("Outside", true)]
    [InlineData("PublishingLayoutPageLookalike", true)]
    [InlineData(null, true)]
    [InlineData("Missing", true)]
    [InlineData("Incomplete", true)]
    public async Task Layout_content_type_cannot_exclude_nonmembers_or_unknowns_during_metadata_refresh(string type, bool descendant)
    {
        var scan = Guid.NewGuid();
        using (var db = database.CreateContext()) await LayoutRoutingEvidence.RecordAuthorityAsync(db, scan);
        var ct = AspxAssetPurpose.LayoutContentType.ToLowerInvariant() + (descendant ? "00aabbccddeeff00112233445566778899" : "");
        var fixture = new MetadataFixture(scan, "_catalogs/masterpage/not-a-handler.aspx", new()
        {
            ["ContentTypeId"] = ct, ["WikiField"] = "<p>Existing eligible page body</p>",
        });
        // Stale ContentType-derived confirmation must not short-circuit the metadata read.
        fixture.Row.ContentTypeId = ct;
        AspxAssetPurpose.Apply(fixture.Row, ct);
        await LayoutRoutingEvidence.InspectAsync(fixture.Row, type);
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { fixture.Row });
        var discovery = new PageScanComponent.PageDiscovery
        {
            PublishingLayoutRuleVersion = 1, Pages = new(), EnrichmentInputs = new(), RemediationCodes = new(),
        };
        await PageScanComponent.RoutePhysicalPageAsync(discovery, fixture.Row, fixture.List);
        await writer.UpdateExistingAsync(new[] { fixture.Row });
        fixture.StreamRequests.Should().Be(1);
        discovery.Pages.Should().ContainSingle().Which.PageType.Should().Be("WikiPage");
        discovery.EnrichmentInputs.Should().ContainSingle().Which.WikiFieldHtml.Should().Be("<p>Existing eligible page body</p>");
        discovery.RemediationCodes.Should().BeEquivalentTo("CP2");
        var retained = (await writer.ReadPagesAsync(scan, Site, Web)).Single();
        retained.PublishingLayoutFamily.Should().Be(type is "Outside" or "PublishingLayoutPageLookalike" ? "NonMember" : "Unknown");
        retained.AssetPurpose.Should().Be("Unknown", "neither nonmembership nor Page Layout ContentType proves content-page purpose");
        retained.AssetPurposeStatus.Should().Be("Unknown");
        retained.AssessmentStatus.Should().Be("Complete", "unknown purpose does not suppress existing eligible page assessment");
        retained.ContentTypeId.Should().Be(ct);
        retained.PageTypeEvidenceJson.Should().Be(fixture.Row.PageTypeEvidenceJson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Missing")]
    [InlineData("Incomplete")]
    public async Task Publishing_metadata_preserves_classification_but_cannot_confirm_unknown_purpose(string type)
    {
        var fixture = new MetadataFixture(Guid.NewGuid(), "Pages/article.aspx", new() { ["ContentTypeId"] = PublishingContentType });
        await LayoutRoutingEvidence.InspectAsync(fixture.Row, type);
        var input = await PageScanComponent.LoadPhysicalPageAsync(fixture.List, fixture.Row, null, true, ruleVersion: 1);
        input.Page.PageType.Should().Be("PublishingPage");
        fixture.Row.PublishingLayoutFamily.Should().Be("Unknown");
        fixture.Row.AssetPurpose.Should().Be("Unknown");
        fixture.Row.AssetPurposeStatus.Should().Be("Unknown");
        fixture.Row.PageTypeReason.Should().NotBe("NotEvaluated");
    }
}
