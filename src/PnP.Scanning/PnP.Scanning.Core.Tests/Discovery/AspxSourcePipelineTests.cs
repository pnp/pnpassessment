using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "PageInherits")]
public sealed class AspxSourcePipelineTests : IClassFixture<ScanContextFixture>
{
    private readonly ScanContextFixture database;
    public AspxSourcePipelineTests(ScanContextFixture database) => this.database = database;

    [Fact]
    public async Task Real_assessment_discovery_acquires_system_form_view_and_other_files_before_item_admission()
    {
        var scan = Guid.NewGuid();
        using var provider = new SourceProvider();
        var authority = new Scan { ScanId = scan, PublishingLayoutRuleVersion = 1 };
        using (var db = database.CreateContext())
        {
            db.Scans.Add(authority);
            await db.SaveChangesAsync();
        }
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var captured = new List<(ClassicPageDiscovery Row, AspxSourceReadResult Read)>();
        var calls = new List<string>();
        var callback = AspxSourceAcquisition.ForScan(authority, async (row, token) =>
        {
            row.AssessmentStatus.Should().BeNull("source acquisition occurs before body/list-item admission");
            (await writer.ReadPagesAsync(scan, "https://example.com/sites/synthetic", "/sites/synthetic", token))
                .Should().Contain(value => value.RecordKey == row.RecordKey, "existence is already committed");
            row.DiscoveryObservation.DiscoveryRecord.Should().NotBeNull();
            calls.Add(row.FileName);
            var bytes = Encoding.UTF8.GetBytes(row.FileName == "system.aspx"
                ? PublishingLayoutTypeEvidenceTests.Source(PublishingLayoutTypeEvidenceTests.Root)
                : "<%@ Page Language='C#' %>");
            var read = await AspxSourceReaderTests.Capture(bytes, row);
            captured.Add((row, read));
            return read;
        });
        await new AssessmentWebDiscovery(scan, "https://example.com/sites/synthetic", "/sites/synthetic",
            writer, callback, authority.PublishingLayoutRuleVersion).RunAsync(provider, default);
        calls.Should().Equal("system.aspx", "form.aspx", "view.aspx", "other.aspx");
        captured.Should().HaveCount(4);
        captured.Should().OnlyContain(value => value.Read.IsReliableSource && value.Row.SourceReads.Count == 1);
        captured[0].Row.ListItemId.Should().BeNull();
        captured[1].Row.ListItemId.Should().BeNull();
        captured[2].Row.ListItemId.Should().Be(7);
        captured[3].Row.ListItemId.Should().BeNull();
        captured[0].Row.SourceReads.Single().Discovery.DiscoveryRecord.SourceObjectId.Should().Be("system.aspx");
        var stored = await writer.ReadPagesAsync(scan, "https://example.com/sites/synthetic", "/sites/synthetic");
        stored.Should().HaveCount(4).And.OnlyContain(value => value.DiscoveryStatus == "Discovered");
        stored.Single(value => value.FileName == "system.aspx").PublishingLayoutFamily.Should().Be("Member");
        stored.Single(value => value.FileName == "form.aspx").PageTypeReason.Should().Contain("InheritsMissing");
        stored.Where(value => value.FileName != "system.aspx").Should().OnlyContain(value => value.AssessmentStatus == null);
    }

    [Fact]
    public async Task One_file_failure_does_not_erase_a_successful_facet_or_block_unrelated_files()
    {
        var scan = Guid.NewGuid();
        var authority = new Scan { ScanId = scan, PublishingLayoutRuleVersion = 1 };
        using (var db = database.CreateContext())
        {
            db.Scans.Add(authority);
            await db.SaveChangesAsync();
        }
        using var provider = new SourceProvider();
        var observations = new List<ClassicPageDiscovery>();
        var callback = AspxSourceAcquisition.ForScan(authority, async (row, _) =>
        {
            observations.Add(row);
            if (row.FileName == "form.aspx") throw new UnauthorizedAccessException("synthetic form denial");
            if (row.FileName == "view.aspx") throw new IOException("synthetic view failure");
            return await AspxSourceReaderTests.Capture(Encoding.UTF8.GetBytes(
                PublishingLayoutTypeEvidenceTests.Source(PublishingLayoutTypeEvidenceTests.Root)), row);
        });
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await new AssessmentWebDiscovery(scan, "https://example.com/sites/synthetic", "/sites/synthetic",
            writer, callback, 1).RunAsync(provider, default);
        observations.Select(row => row.FileName).Should().Equal("system.aspx", "form.aspx", "view.aspx", "other.aspx");
        observations.Single(row => row.FileName == "form.aspx").SourceReads.Single().TransportState.Should().Be(AspxSourceTransportState.Denied);
        observations.Single(row => row.FileName == "view.aspx").SourceReads.Single().TransportState.Should().Be(AspxSourceTransportState.Failed);
        var stored = await writer.ReadPagesAsync(scan, "https://example.com/sites/synthetic", "/sites/synthetic");
        stored.Should().HaveCount(4).And.OnlyContain(row => row.DiscoveryStatus == "Discovered");
        stored.Single(row => row.FileName == "form.aspx").PageTypeSourceStatus.Should().Be("Denied");
        stored.Single(row => row.FileName == "view.aspx").PageTypeSourceStatus.Should().Be("Failed");
        stored.Single(row => row.FileName == "other.aspx").PublishingLayoutFamily.Should().Be("Member");
        stored.Where(row => row.FileName is "form.aspx" or "view.aspx").Should().OnlyContain(row => row.AssessmentStatus == null);
        (await writer.ReadWebCoverageAsync(scan, "https://example.com/sites/synthetic", "/sites/synthetic")).Should().Be("Complete",
            "source failures cannot change successful file enumeration into a new whole-Web skip");
    }

    [Fact]
    public async Task Cancelled_source_acquisition_preserves_discovered_existence_without_claiming_completion()
    {
        var scan = Guid.NewGuid();
        var authority = new Scan { ScanId = scan, PublishingLayoutRuleVersion = 1 };
        using (var db = database.CreateContext())
        {
            db.Scans.Add(authority);
            await db.SaveChangesAsync();
        }
        using var provider = new SourceProvider();
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var callback = AspxSourceAcquisition.ForScan(authority, (_, _) => throw new OperationCanceledException("synthetic cancellation"));
        var run = () => new AssessmentWebDiscovery(scan, "https://example.com/sites/synthetic", "/sites/synthetic",
            writer, callback, 1).RunAsync(provider, default);
        await run.Should().ThrowAsync<OperationCanceledException>();
        var rows = await writer.ReadPagesAsync(scan, "https://example.com/sites/synthetic", "/sites/synthetic");
        rows.Should().HaveCount(4).And.OnlyContain(row => row.PageTypeEvidenceJson == null && row.AssessmentStatus == null);
        using var context = database.CreateContext();
        (await context.ClassicPageDiscoveries.SingleAsync(row => row.ScanId == scan && row.RecordKey == "scope:files"))
            .DiscoveryStatus.Should().Be("Pending");
    }

    [Fact]
    public void New_contracts_are_explicitly_transient_and_do_not_change_the_ef_schema()
    {
        using var db = database.CreateContext();
        var entity = db.Model.FindEntityType(typeof(ClassicPageDiscovery));
        entity.FindProperty(nameof(ClassicPageDiscovery.DiscoveryObservation)).Should().BeNull();
        entity.FindNavigation(nameof(ClassicPageDiscovery.DiscoveryObservation)).Should().BeNull();
        entity.FindProperty(nameof(ClassicPageDiscovery.SourceReads)).Should().BeNull();
        entity.FindNavigation(nameof(ClassicPageDiscovery.SourceReads)).Should().BeNull();
        db.Model.GetEntityTypes().Should().NotContain(value => value.ClrType == typeof(AspxSourceReadResult) ||
            value.ClrType == typeof(AspxFileObservation));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_pnp_context_downloads_by_unique_id_without_requesting_a_list_item(bool returnVersionAndLength)
    {
        using var fixture = new DiscoveryTransportFixture();
        using var factory = fixture.CreateFactory(false);
        using var context = await factory.CreateContextAsync(DiscoveryTransportFixture.WebUrl, default);
        var bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("<%@ Page Language='C#' %>")).ToArray();
        fixture.Transport.Respond = request => request.RequestUri.AbsolutePath.EndsWith("/download.aspx")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : request.RequestUri.AbsoluteUri.Contains("getfileby", StringComparison.OrdinalIgnoreCase)
                ? DiscoveryTransportFixture.RecordingTransport.Json(JsonSerializer.Serialize(returnVersionAndLength
                    ? new { UniqueId = DiscoveryTransportFixture.FileId, Name = "default.aspx",
                        ServerRelativeUrl = "/sites/discovery/default.aspx", Length = bytes.Length,
                        ETag = "\"synthetic-version\"", MajorVersion = 2, MinorVersion = 3 } as object
                    : new { UniqueId = DiscoveryTransportFixture.FileId, Name = "default.aspx",
                        ServerRelativeUrl = "/sites/discovery/default.aspx" }))
                : MetadataResponse(request);
        var row = AspxSourceReaderTests.Row(AspxSourceReaderTests.Record() with
        {
            PhysicalLocator = "/sites/discovery/default.aspx",
        });
        var read = await PnPAspxSourceReader.ReadAsync(context, AspxSourceReaderTests.SiteId, AspxSourceReaderTests.WebId, row, default);
        read.TransportState.Should().Be(AspxSourceTransportState.Complete);
        read.IsReliableSource.Should().BeTrue();
        read.OriginalBytes.Should().Equal(bytes);
        read.Decoding.BomByteCount.Should().Be(3);
        read.PhysicalIdentity.FileUniqueId.Should().Be(AspxSourceReaderTests.FileId);
        read.PhysicalIdentity.ItemIdentityState.Should().Be("Unavailable");
        read.ExpectedByteLength.Should().Be(returnVersionAndLength ? bytes.Length : null);
        read.Version.State.Should().Be(returnVersionAndLength ? "ObservedVersion" : "ObservationTimeOnly");
        if (returnVersionAndLength)
        {
            read.Version.ETag.Should().Be("\"synthetic-version\"");
            read.Version.MajorVersion.Should().Be(2);
            read.Version.MinorVersion.Should().Be(3);
        }
        fixture.Transport.Requests.Should().ContainSingle(request => request.Uri.AbsolutePath.EndsWith("/download.aspx"))
            .Which.Uri.Query.Should().Contain("UniqueId=" + DiscoveryTransportFixture.FileId);
        fixture.Transport.Requests.Should().NotContain(request => request.Uri.AbsolutePath.EndsWith("/default.aspx"));
        fixture.Transport.Requests.Should().NotContain(request => request.Uri.AbsoluteUri.Contains("ListItemAllFields", StringComparison.OrdinalIgnoreCase));
        fixture.AssertHeaders(false);
    }

    [Fact]
    public async Task Pnp_identity_change_retains_both_identities_and_prevents_the_wrong_file_download()
    {
        using var fixture = new DiscoveryTransportFixture();
        using var factory = fixture.CreateFactory(false);
        using var context = await factory.CreateContextAsync(DiscoveryTransportFixture.WebUrl, default);
        var changed = Guid.Parse("55555555-5555-5555-5555-555555555555");
        fixture.Transport.Respond = request => request.RequestUri.AbsoluteUri.Contains("getfileby", StringComparison.OrdinalIgnoreCase)
            ? DiscoveryTransportFixture.RecordingTransport.Json(JsonSerializer.Serialize(new
            {
                UniqueId = changed, Name = "default.aspx", ServerRelativeUrl = "/sites/discovery/default.aspx",
                ETag = "\"replacement-file\"", MajorVersion = 1, MinorVersion = 0,
            })) : MetadataResponse(request);
        var row = AspxSourceReaderTests.Row(AspxSourceReaderTests.Record() with { PhysicalLocator = "/sites/discovery/default.aspx" });
        var result = await PnPAspxSourceReader.ReadAsync(context, AspxSourceReaderTests.SiteId, AspxSourceReaderTests.WebId, row, default);
        result.TransportState.Should().Be(AspxSourceTransportState.Failed);
        result.TransportReason.Should().Be("SourceFileIdentityChanged");
        result.PhysicalIdentity.FileUniqueId.Should().Be(changed);
        result.Discovery.Identity.FileUniqueId.Should().Be(AspxSourceReaderTests.FileId);
        result.Discovery.DiscoveryRecord.FileUniqueId.Should().Be(DiscoveryTransportFixture.FileId);
        result.CapturedByteLength.Should().BeNull();
        result.OriginalBytes.Should().BeNull();
        fixture.Transport.Requests.Should().NotContain(request => request.Uri.AbsolutePath.EndsWith("/download.aspx"));
    }

    [Fact]
    public async Task Scheduled_site_web_identity_mismatch_is_not_attempted()
    {
        using var fixture = new DiscoveryTransportFixture();
        using var factory = fixture.CreateFactory(false);
        using var context = await factory.CreateContextAsync(DiscoveryTransportFixture.WebUrl, default);
        var before = fixture.Transport.Requests.Count;
        var result = await PnPAspxSourceReader.ReadAsync(context, AspxSourceReaderTests.SiteId, Guid.Parse(
            "55555555-5555-5555-5555-555555555555"), AspxSourceReaderTests.Row(), default);
        result.TransportState.Should().Be(AspxSourceTransportState.NotAttempted);
        result.TransportReason.Should().Be("DiscoveryIdentityOutsideScheduledSiteWeb");
        result.CapturedByteLength.Should().BeNull();
        fixture.Transport.Requests.Count.Should().Be(before);
    }

    private static HttpResponseMessage MetadataResponse(HttpRequestMessage request) =>
        DiscoveryTransportFixture.RecordingTransport.Json(request.RequestUri.AbsolutePath.EndsWith("/_api/site")
            ? JsonSerializer.Serialize(new { Id = DiscoveryTransportFixture.SiteId, GroupId = Guid.Empty })
            : request.RequestUri.AbsolutePath.EndsWith("/_api/web")
                ? JsonSerializer.Serialize(new { Id = DiscoveryTransportFixture.WebId, Url = DiscoveryTransportFixture.WebUrl.AbsoluteUri,
                    ServerRelativeUrl = DiscoveryTransportFixture.WebUrl.AbsolutePath, RegionalSettings = new { TimeZone = new { Id = 2 } } })
                : "{\"value\":[]}");

    private sealed class SourceProvider : IAspxDiscoveryProvider
    {
        public DiscoveryScopeRegistration RootScope { get; } = new("web", null, DiscoveryScopeKind.Web, null,
            "/sites/synthetic", "synthetic-permission");
        private readonly DiscoveryScopeRegistration files = new("files", "web", DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles, "/sites/synthetic", "synthetic-permission");
        public Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(DiscoveryScopeRegistration parent, CancellationToken token) =>
            Task.FromResult(parent.ScopeKey == "web"
                ? new DiscoveryChildEnumerationResult(DiscoveryScopeKind.Folder,
                    new[] { new DiscoveryChildExpectation(files.ScopeKey, files.Kind, files.SourceKind, files.Locator, files.PermissionContext) },
                    new[] { files }, DiscoveryTerminalOutcome.Complete, parent.PermissionContext)
                : new DiscoveryChildEnumerationResult(DiscoveryScopeKind.Folder, Array.Empty<DiscoveryChildExpectation>(),
                    Array.Empty<DiscoveryScopeRegistration>(), DiscoveryTerminalOutcome.Empty, parent.PermissionContext));
        public IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration scope) => new Source(scope);
        public void Dispose() { }
        private sealed class Source : IRawDiscoverySource
        {
            private readonly DiscoveryScopeRegistration scope;
            internal Source(DiscoveryScopeRegistration scope) => this.scope = scope;
            public DiscoverySourceKind Kind => DiscoverySourceKind.RawListLibraryFiles;
            public async IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var names = new[] { "system.aspx", "form.aspx", "view.aspx", "other.aspx" };
                var records = names.Select((name, index) => AspxSourceReaderTests.Record() with
                {
                    SourceObjectId = name,
                    FileUniqueId = new Guid(index + 1, 0, 0, new byte[8]).ToString("D"),
                    FileName = name,
                    PhysicalLocator = "/sites/synthetic/" + name,
                    ListId = name == "view.aspx" ? AspxSourceReaderTests.ListId : null,
                    ListItemId = name == "view.aspx" ? 7 : null,
                    ObservationMethod = name == "form.aspx" ? "SyntheticForm" : name == "view.aspx" ? "SyntheticView" : "SyntheticRawFiles",
                }).ToArray();
                yield return new RawDiscoveryBatch(0, "synthetic-request", "synthetic-response", records, true, DiscoveryTerminalOutcome.Complete);
                await Task.CompletedTask;
            }
        }
    }
}
