using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "PageInherits")]
public sealed class PageSourceNativePipelineTests
{
    [Fact]
    public async Task Existing_per_file_callback_durably_enriches_system_Form_View_and_other_files_without_cross_file_failure_or_profile_overwrite()
    {
        using var fixture = new PageSourcePersistenceFixture();
        var scan = await fixture.SeedAsync();
        using var provider = new Provider();
        var writer = fixture.Writer();
        var calls = new List<string>();
        var callback = AspxSourceAcquisition.ForAssessmentScan(scan, async (row, _) =>
        {
            var before = await fixture.ReopenAsync(row.RecordKey);
            before.SourceEvidenceState.Should().Be("DiscoveryOnly");
            before.ReadSourceEvidence().Reads.Should().BeEmpty();
            before.ReadSourceEvidence().DiscoveryObservations.Single().DiscoveryRecord.SourceObjectId.Should().Be(row.FileName);
            row.AssessmentStatus.Should().BeNull("the shared callback runs before ListItem admission");
            calls.Add(row.FileName);
            if (row.FileName == "form.aspx") throw new UnauthorizedAccessException("Synthetic form denial");
            if (row.FileName == "view.aspx") throw new IOException("Synthetic view source failure");
            var text = row.FileName == "system.aspx" ? "<%@ Page Inherits='Synthetic.System' %>" :
                row.FileName == "malformed.aspx" ? "<%@ Page Inherits='Synthetic.Bad' Inherits='Synthetic.Bad' %>" : "<%@ Page %>";
            return await PageSourcePersistenceFixture.Capture(row, Encoding.UTF8.GetBytes(text));
        });
        await new AssessmentWebDiscovery(fixture.ScanId, PageSourcePersistenceFixture.Site, PageSourcePersistenceFixture.Web,
            writer, callback, 1).RunAsync(provider, default);
        calls.Should().Equal("system.aspx", "form.aspx", "view.aspx", "malformed.aspx", "other.aspx");
        var rows = await writer.ReadPagesAsync(fixture.ScanId, PageSourcePersistenceFixture.Site, PageSourcePersistenceFixture.Web);
        rows.Should().HaveCount(5).And.OnlyContain(row => row.DiscoveryStatus == "Discovered");
        rows.Should().OnlyContain(row => row.AssessmentStatus == null, "source/parser failure adds no whole-page skip");
        rows.Single(row => row.FileName == "system.aspx").ListItemId.Should().BeNull();
        rows.Single(row => row.FileName == "form.aspx").ListItemId.Should().BeNull();
        rows.Single(row => row.FileName == "view.aspx").ListItemId.Should().Be(7);
        rows.Single(row => row.FileName == "system.aspx").DeclaredInherits.Should().Be("Synthetic.System");
        rows.Single(row => row.FileName == "form.aspx").SourceReadState.Should().Be("Denied");
        rows.Single(row => row.FileName == "view.aspx").SourceReadState.Should().Be("Failed");
        rows.Single(row => row.FileName == "malformed.aspx").SourceReadState.Should().Be("Complete");
        rows.Single(row => row.FileName == "malformed.aspx").PageParseState.Should().Be("DuplicateAttribute");
        rows.Single(row => row.FileName == "malformed.aspx").TypeSource.Should().Be("Unknown");
        rows.Single(row => row.FileName == "other.aspx").TypeSource.Should().Be("FrameworkDefault");
        rows.Single(row => row.FileName == "other.aspx").PublishingLayoutFamily.Should().Be("Unknown");
        rows.Single(row => row.FileName == "other.aspx").PageTypeReason.Should().Contain("InheritsMissing");
        (await writer.ReadWebCoverageAsync(fixture.ScanId, PageSourcePersistenceFixture.Site, PageSourcePersistenceFixture.Web))
            .Should().Be("Complete", "independent source errors must not change file enumeration coverage");
        var system = rows.Single(row => row.FileName == "system.aspx");
        var originalSource = system.SourceEvidenceJson;
        await writer.UpdateExistingAsync(new[]
        {
            new ClassicPageDiscovery
            {
                ScanId = fixture.ScanId, RecordKey = system.RecordKey, RowType = "Page", DiscoveryStatus = "Discovered",
                ContentTypeId = AspxAssetPurpose.LayoutContentType, PageType = "WebPartPage", AssessmentStatus = "Complete",
                EvidenceJson = """{"SyntheticContentType":"PageLayout","SyntheticLayout":"ProfileLayout","SyntheticWebPartType":"Unrelated.Profile"}""",
            },
        });
        var preserved = await fixture.ReopenAsync(system.RecordKey);
        preserved.SourceEvidenceJson.Should().Be(originalSource);
        preserved.DeclaredInherits.Should().Be("Synthetic.System");
        preserved.TypeSource.Should().Be("Declared");
        preserved.SourceReadState.Should().Be("Complete");
        preserved.PublishingLayoutFamily.Should().Be("Unknown", "metadata profiles cannot manufacture CLR-family evidence");
        var exported = (await fixture.ExportAsync()).Where(row => row["RowType"] == "Page").ToArray();
        exported.Should().HaveCount(5);
        exported.Single(row => row["FileName"] == "system.aspx")["DeclaredInherits"].Should().Be("Synthetic.System");
        exported.Single(row => row["FileName"] == "form.aspx")["SourceReadState"].Should().Be("Denied");
        exported.Single(row => row["FileName"] == "view.aspx")["SourceReadState"].Should().Be("Failed");
        exported.Single(row => row["FileName"] == "other.aspx")["ObservedHandlerState"].Should().Be("ServerOnlyUnavailable");
        using var db = fixture.CreateContext();
        (await db.ClassicPages.CountAsync()).Should().Be(0, "the callback does not create a second scanner or body extraction");
    }

    private sealed class Provider : IAspxDiscoveryProvider
    {
        public DiscoveryScopeRegistration RootScope { get; } = new("web", null, DiscoveryScopeKind.Web, null,
            PageSourcePersistenceFixture.Site, "synthetic");
        private readonly DiscoveryScopeRegistration folder = new("files", "web", DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles, "/sites/source", "synthetic");
        private readonly RawDiscoveryRecord[] records = new[] { "system", "form", "view", "malformed", "other" }
            .Select((name, index) => new RawDiscoveryRecord(name + ".aspx", $"33333333-3333-3333-3333-{index + 1:000000000000}",
                "files", name + ".aspx", "/sites/source/" + name + ".aspx", true, "synthetic",
                SiteCollectionId: PageSourcePersistenceFixture.SiteId, WebId: PageSourcePersistenceFixture.WebId,
                ListId: name == "view" ? PageSourcePersistenceFixture.ListId : null,
                ListItemId: name == "view" ? 7 : null, ObservationMethod: "Synthetic" + name)).ToArray();
        public Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(DiscoveryScopeRegistration parent, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiscoveryChildEnumerationResult(DiscoveryScopeKind.Folder, Array.Empty<DiscoveryChildExpectation>(),
                parent == RootScope ? new[] { folder } : Array.Empty<DiscoveryScopeRegistration>(), DiscoveryTerminalOutcome.Complete, "synthetic"));
        public IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface) => new Batch(records);
        public void Dispose() { }
    }

    private sealed class Batch(RawDiscoveryRecord[] records) : IRawDiscoverySource
    {
        public async IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new(1, null, null, records, true, DiscoveryTerminalOutcome.Complete);
        }
    }
}
