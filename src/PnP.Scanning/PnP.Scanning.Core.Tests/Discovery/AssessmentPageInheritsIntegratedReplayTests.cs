using CsvHelper.Configuration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Scan = PnP.Scanning.Core.Storage.Scan;

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
    public async Task Integrated_source_enrichment_precedes_real_item_admission_and_survives_native_reopen_and_export(string path, bool hasItem)
    {
        var metadata = new MetadataFixture(Guid.NewGuid(), path,
            new() { ["ContentTypeId"] = "0x0101", ["WikiField"] = "<p>Synthetic body facet</p>" });
        if (!hasItem) { metadata.Row.ListId = null; metadata.Row.ListItemId = null; }
        var original = RecordForIntegratedReplay(metadata.Row);
        var authority = new Scan
        {
            ScanId = metadata.Row.ScanId, PublishingLayoutRuleVersion = 1, PageSourceEvidenceVersion = 1,
            PublishingLayoutTypeCatalogJson = new PublishingLayoutTypeCatalog().ToJson(),
            PageBaseTypeConfigurationJson = new PageBaseTypeConfiguration(new[]
            {
                new PageBaseTypeConfigurationEvidence("EffectiveOverride", "Synthetic.Configured", "Synthetic frozen configuration",
                    new(metadata.Row.SiteCollectionId.ToString(), metadata.Row.WebId.ToString(),
                        metadata.Row.FileUniqueId.ToString(), metadata.Row.Url), "Synthetic exact file"),
            }).ToJson(),
        };
        using (var db = database.CreateContext()) { db.Scans.Add(authority); await db.SaveChangesAsync(); }
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("<%@ Page Inherits=' Synthetic.Direct ' %>")).ToArray();
        var acquire = AspxSourceAcquisition.ForAssessmentScan(authority, async (row, token) =>
        {
            metadata.ItemRequests.Should().Be(0, "discovery source enrichment must precede native item admission");
            row.AssessmentStatus.Should().BeNull();
            (await writer.ReadPagesAsync(authority.ScanId, Site, Web)).Should().ContainSingle()
                .Which.SourceEvidenceState.Should().Be("DiscoveryOnly");
            return await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
                new(DateTimeOffset.Parse("2026-02-03T06:35:07.7654321+02:30", CultureInfo.InvariantCulture), "\"synthetic-version\"", 2, 1),
                _ => Task.FromResult<Stream>(new MemoryStream(bytes)), token, bytes.LongLength);
        });
        using var provider = new SyntheticPageInheritsProvider(Site, new[] { original });
        await new AssessmentWebDiscovery(authority.ScanId, Site, Web, writer, acquire, 1).RunAsync(provider, default);
        metadata.ItemRequests.Should().Be(0);
        metadata.Row = (await writer.ReadPagesAsync(authority.ScanId, Site, Web)).Single();
        metadata.Row.SourceReads.Should().BeEmpty("the native reopen has no transient acquisition objects");
        metadata.Row.DeclaredInherits.Should().Be(" Synthetic.Direct ");
        metadata.Row.TypeSource.Should().Be("Declared", "the explicit source declaration takes precedence over applicable configuration");
        var routing = IntegratedRouting();
        await PageScanComponent.RoutePhysicalPageAsync(routing, metadata.Row, metadata.List);
        metadata.ItemRequests.Should().Be(hasItem ? 1 : 0);
        metadata.Row.AssessmentStatus.Should().Be(hasItem ? "Complete" : "NotApplicable");
        routing.Pages.Should().HaveCount(hasItem ? 1 : 0);
        if (hasItem) routing.EnrichmentInputs.Single().WikiFieldHtml.Should().Be("<p>Synthetic body facet</p>");
        await writer.UpdateExistingAsync(new[] { metadata.Row });
        using (var db = database.CreateContext())
        {
            foreach (var page in routing.Pages) PageScanComponent.ApplyDiscoveryState(page, metadata.Row);
            await StorageManager.StorePageInformationAsync(db, routing.Pages);
        }
        var reopened = (await writer.ReadPagesAsync(authority.ScanId, Site, Web)).Single();
        reopened.DeclaredInherits.Should().Be(" Synthetic.Direct ");
        reopened.BaseType.Should().Be("Synthetic.Direct");
        reopened.PageBaseTypeProvenance.Should().Be("Synthetic frozen configuration");
        reopened.PublishingLayoutFamily.Should().Be("Unknown", "metadata/body classification cannot manufacture family proof");
        var retrieved = await writer.ReadSourceArtifactAsync(authority.ScanId, reopened.RecordKey, reopened.SourceObservationId);
        retrieved.Should().Equal(bytes);
        reopened.SourceRawDigest.Should().Be(Convert.ToHexString(SHA256.HashData(retrieved)));
        var csv = await ExportIntegratedReplayAsync(authority.ScanId);
        var row = csv.Single(value => value["RowType"] == "Page");
        row["FileName"].Should().Be(Path.GetFileName(path));
        row["ListItemId"].Should().Be(hasItem ? "1" : "");
        row["DeclaredInherits"].Should().Be(" Synthetic.Direct ");
        row["BaseType"].Should().Be("Synthetic.Direct");
        row["TypeSource"].Should().Be("Declared");
        row["SourceReadState"].Should().Be("Complete");
        row["PageParseState"].Should().Be("Declared");
        row["SourceObservedAtUtc"].Should().Be("2026-02-03T04:05:07.7654321Z");
        row["ConfigurationKnowledgeState"].Should().Be("EffectiveOverride");
        row["ConfigurationApplicability"].Should().Be("Applicable");
        row["AssessmentStatus"].Should().Be(hasItem ? "Complete" : "NotApplicable");
        row["ObservedHandlerState"].Should().Be("ServerOnlyUnavailable");
        using var json = JsonDocument.Parse(row["SourceEvidenceJson"]);
        var read = json.RootElement.GetProperty("Reads")[0];
        read.GetProperty("OriginalBytes").GetBytesFromBase64().Should().Equal(bytes);
        read.GetProperty("Discovery").GetProperty("DiscoveryRecord").GetProperty("PhysicalLocator").GetString().Should().Be(Web + "/" + path);
        (await writer.ReadWebCoverageAsync(authority.ScanId, Site, Web)).Should().Be("Complete");
    }

    [Fact]
    [Trait("Category", "PageInherits")]
    public async Task Integrated_source_failure_remains_visible_after_successful_item_body_while_item_failure_retains_successful_source()
    {
        var scanId = Guid.NewGuid();
        var bodySuccess = new MetadataFixture(scanId, "Pages/source-failed.aspx",
            new() { ["ContentTypeId"] = "0x0101", ["WikiField"] = "<p>Successful synthetic body</p>" });
        var itemFailure = new MetadataFixture(scanId, "Pages/item-failed.aspx", new()) { ReturnedId = null };
        var authority = new Scan { ScanId = scanId, PublishingLayoutRuleVersion = 1, PageSourceEvidenceVersion = 1,
            PublishingLayoutTypeCatalogJson = new PublishingLayoutTypeCatalog().ToJson(), PageBaseTypeConfigurationJson = new PageBaseTypeConfiguration().ToJson() };
        using (var db = database.CreateContext()) { db.Scans.Add(authority); await db.SaveChangesAsync(); }
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var bytes = Encoding.UTF8.GetBytes("<%@ Page Inherits='Synthetic.Success' %>");
        var acquire = AspxSourceAcquisition.ForAssessmentScan(authority, async (row, token) =>
        {
            bodySuccess.ItemRequests.Should().Be(0);
            itemFailure.ItemRequests.Should().Be(0);
            if (row.FileName == "source-failed.aspx") throw new IOException("Synthetic source facet failure");
            return await AspxSourceReader.ReadAsync(row.DiscoveryObservation, row.DiscoveryObservation.Identity,
                new(PageSourcePersistenceFixture.ReadTime), _ => Task.FromResult<Stream>(new MemoryStream(bytes)), token, bytes.LongLength);
        });
        using var provider = new SyntheticPageInheritsProvider(Site, new[]
            { RecordForIntegratedReplay(bodySuccess.Row), RecordForIntegratedReplay(itemFailure.Row) });
        await new AssessmentWebDiscovery(scanId, Site, Web, writer, acquire, 1).RunAsync(provider, default);
        var rows = await writer.ReadPagesAsync(scanId, Site, Web);
        bodySuccess.Row = rows.Single(value => value.FileName == "source-failed.aspx");
        itemFailure.Row = rows.Single(value => value.FileName == "item-failed.aspx");
        var routing = IntegratedRouting();
        await PageScanComponent.RoutePhysicalPageAsync(routing, bodySuccess.Row, bodySuccess.List);
        bodySuccess.Row.AssessmentStatus.Should().Be("Complete");
        routing.EnrichmentInputs.Single().WikiFieldHtml.Should().Be("<p>Successful synthetic body</p>");
        await writer.UpdateExistingAsync(new[] { bodySuccess.Row });
        var load = () => PageScanComponent.RoutePhysicalPageAsync(routing, itemFailure.Row, itemFailure.List);
        await load.Should().ThrowAsync<InvalidDataException>().WithMessage("*not returned*");
        // Native failure finalization touches only unfinished rows. It must not erase source evidence
        // or mark the already successful unrelated body facet as failed.
        (await writer.FailUnassessedPagesAsync(scanId, Site, Web, new IOException("Synthetic item facet failure"), "PageMetadata"))
            .Should().Be(1);
        var csv = await ExportIntegratedReplayAsync(scanId);
        var sourceFailed = csv.Single(value => value["FileName"] == "source-failed.aspx");
        sourceFailed["SourceReadState"].Should().Be("Failed");
        sourceFailed["SourceReadReason"].Should().Contain("Synthetic source facet failure");
        sourceFailed["TypeSource"].Should().Be("Unknown");
        sourceFailed["AssessmentStatus"].Should().Be("Complete");
        var itemFailed = csv.Single(value => value["FileName"] == "item-failed.aspx");
        itemFailed["SourceReadState"].Should().Be("Complete");
        itemFailed["DeclaredInherits"].Should().Be("Synthetic.Success");
        itemFailed["TypeSource"].Should().Be("Declared");
        itemFailed["AssessmentStatus"].Should().Be("Failed");
        itemFailed["ErrorDetail"].Should().Contain("Synthetic item facet failure");
        var reopened = (await writer.ReadPagesAsync(scanId, Site, Web)).Single(value => value.FileName == "item-failed.aspx");
        (await writer.ReadSourceArtifactAsync(scanId, reopened.RecordKey, reopened.SourceObservationId)).Should().Equal(bytes);
        (await writer.ReadWebCoverageAsync(scanId, Site, Web)).Should().Be("Complete");
    }

    private static PageScanComponent.PageDiscovery IntegratedRouting() => new()
    {
        PublishingLayoutRuleVersion = 1, Pages = new(), EnrichmentInputs = new(), RemediationCodes = new(), SkipUserInformation = true,
    };

    private static RawDiscoveryRecord RecordForIntegratedReplay(ClassicPageDiscovery row) => new(
        row.FileName, row.FileUniqueId.Value.ToString("D"), "synthetic-container", row.FileName, row.Url, true,
        "offline-synthetic", SiteCollectionId: row.SiteCollectionId, WebId: row.WebId, ListId: row.ListId, ListItemId: row.ListItemId);

    private async Task<IReadOnlyList<Dictionary<string, string>>> ExportIntegratedReplayAsync(Guid scanId)
    {
        var directory = Path.Combine(Path.GetTempPath(), "synthetic-integrated-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var db = database.CreateContext())
                await ReportManager.ExportClassicReportDataAsync(db, scanId, directory, new CsvConfiguration(CultureInfo.InvariantCulture));
            return PageSourcePersistenceFixture.ReadIndependentCsv(Path.Combine(directory, "discovery.csv")).Rows;
        }
        finally { Directory.Delete(directory, true); }
    }
}
