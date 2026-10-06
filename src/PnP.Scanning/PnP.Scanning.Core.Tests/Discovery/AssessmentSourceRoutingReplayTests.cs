using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using System.Text;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed partial class AssessmentPageMetadataReplayTests
{
    [Theory]
    [Trait("Category", "PageInherits")]
    [InlineData("_catalogs/masterpage/system.aspx", true)]
    [InlineData("_catalogs/masterpage/system.aspx", false)]
    [InlineData("Forms/form.aspx", true)]
    [InlineData("Forms/form.aspx", false)]
    [InlineData("Views/view.aspx", true)]
    [InlineData("Views/view.aspx", false)]
    [InlineData("Pages/other.aspx", true)]
    [InlineData("Pages/other.aspx", false)]
    public async Task New_acquisition_adapter_precedes_real_item_admission_for_every_file_kind(string path, bool hasItem)
    {
        var fixture = new MetadataFixture(Guid.NewGuid(), path,
            new() { ["ContentTypeId"] = "0x0101", ["WikiField"] = "<p>Synthetic page body</p>" });
        if (!hasItem)
        {
            fixture.Row.ListId = null;
            fixture.Row.ListItemId = null;
        }
        var original = AspxFileObservation.FromDiscovery(fixture.Row, new RawDiscoveryRecord(
            path, fixture.Row.FileUniqueId.Value.ToString("D"), "synthetic-container",
            fixture.Row.FileName, fixture.Row.Url, true, "synthetic-permission",
            SiteCollectionId: fixture.Row.SiteCollectionId, WebId: fixture.Row.WebId,
            ListId: fixture.Row.ListId, ListItemId: fixture.Row.ListItemId));
        fixture.Row.DiscoveryObservation = original;
        var callback = AspxSourceAcquisition.ForScan(new Core.Storage.Scan { PublishingLayoutRuleVersion = 1 },
            async (row, _) =>
            {
                fixture.ItemRequests.Should().Be(0, "the adapter must run before list-item admission");
                return await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes("<%@ Page Inherits='Synthetic.Page' %>"), row);
            });
        await callback(fixture.Row, default);
        fixture.ItemRequests.Should().Be(0);
        fixture.Row.SourceReads.Should().ContainSingle().Which.IsReliableSource.Should().BeTrue();
        var routing = new PageScanComponent.PageDiscovery
        {
            PublishingLayoutRuleVersion = 1, Pages = new(), EnrichmentInputs = new(), RemediationCodes = new(),
            SkipUserInformation = true,
        };
        await PageScanComponent.RoutePhysicalPageAsync(routing, fixture.Row, fixture.List);
        fixture.ItemRequests.Should().Be(hasItem ? 1 : 0);
        fixture.Row.AssessmentStatus.Should().Be(hasItem ? "Complete" : "NotApplicable",
            "missing items retain the inherited routing disposition, not a new source-reading skip");
        fixture.Row.SourceReads.Single().Discovery.Should().BeSameAs(original);
        fixture.Row.SourceReads.Single().PhysicalIdentity.FileUniqueId.Should().Be(fixture.Row.FileUniqueId);
        fixture.Row.PageTypeReason.Should().Contain("UnresolvedIdentity", "item/content-type evidence cannot overwrite direct source evidence");
    }

    [Fact]
    [Trait("Category", "PageInherits")]
    public async Task Source_failure_does_not_skip_real_successful_item_body_and_metadata_acquisition()
    {
        var fixture = new MetadataFixture(Guid.NewGuid(), "Pages/body.aspx",
            new() { ["ContentTypeId"] = "0x0101", ["WikiField"] = "<p>Successful synthetic body facet</p>" });
        var callback = AspxSourceAcquisition.ForScan(new Core.Storage.Scan { PublishingLayoutRuleVersion = 1 },
            (_, _) => throw new IOException("synthetic source facet failure"));
        await callback(fixture.Row, default);
        fixture.ItemRequests.Should().Be(0);
        var routing = new PageScanComponent.PageDiscovery
        {
            PublishingLayoutRuleVersion = 1, Pages = new(), EnrichmentInputs = new(), RemediationCodes = new(),
            SkipUserInformation = true,
        };
        await PageScanComponent.RoutePhysicalPageAsync(routing, fixture.Row, fixture.List);
        fixture.ItemRequests.Should().Be(1);
        routing.Pages.Should().ContainSingle();
        routing.EnrichmentInputs.Should().ContainSingle().Which.WikiFieldHtml.Should().Contain("Successful synthetic body facet");
        fixture.Row.AssessmentStatus.Should().Be("Complete");
        fixture.Row.SourceReads.Should().ContainSingle().Which.TransportState.Should().Be(AspxSourceTransportState.Failed);
        fixture.Row.PageTypeSourceStatus.Should().Be("Failed");
        fixture.Row.PageTypeReason.Should().Contain("synthetic source facet failure");
    }
}
