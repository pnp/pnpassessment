using FluentAssertions;
using PnP.Core.Services;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PnP.Scanning.Core.Tests.Fixtures;

/// <summary>
/// Synthetic discovery inputs, real assessment acquisition/parser/writer, disk close/reopen and
/// native export. The real SDK's physical download ends in the offline recording transport.
/// Only scan initialization is seeded; no projected discovery/source/result rows are seeded.
/// </summary>
internal sealed class PageInheritsIntegrationFixture : IDisposable
{
    internal const string Site = "https://example.com/sites/discovery";
    internal const string Web = "/sites/discovery";
    internal static readonly Guid SiteId = Guid.Parse(DiscoveryTransportFixture.SiteId);
    internal static readonly Guid WebId = Guid.Parse(DiscoveryTransportFixture.WebId);
    internal static readonly Guid ListId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    internal PageSourcePersistenceFixture Database { get; } = new();
    internal DiscoveryTransportFixture Http { get; } = new();
    internal AssessmentDiscoveryWriter Writer => Database.Writer();
    internal Scan Scan { get; private set; }
    internal List<string> AcquisitionCalls { get; } = new();
    internal List<ClassicPageDiscovery> AcquiredRows { get; } = new();
    private PnPContextSharePointAspxRestClientFactory factory;
    private PnPContext context;
    private Input[] sdkInputs;
    internal int BootstrapRequestCount { get; private set; }

    internal sealed record Input(string Name, string Path, Guid FileId, byte[] Bytes, int? Item = null,
        bool ReturnVersionAndLength = true, string ETag = "\"synthetic, version\"", int Major = 3, int Minor = 7,
        string SourcePath = null, string SourceName = null, Guid? SourceId = null, Guid? DiscoveryWebId = null,
        bool ReturnUniqueId = true, HttpStatusCode DownloadStatus = HttpStatusCode.OK)
    {
        internal RawDiscoveryRecord Record() => new(Name, FileId.ToString("D"), "synthetic-container",
            Name, Path, true, "offline-synthetic",
            new Dictionary<string, string> { ["synthetic, \"input\""] = "retained\r\nmetadata" },
            SiteId, DiscoveryWebId ?? WebId, Item.HasValue ? ListId : null, ListItemId: Item,
            LibraryHidden: Path.Contains("/Forms/", StringComparison.Ordinal),
            ObservationMethod: "SyntheticDiscoveryInput");
    }

    internal async Task InitializeAsync(PageBaseTypeConfiguration configuration = null) =>
        Scan = await Database.SeedAsync(configuration);

    internal async Task UseSdkAsync(params Input[] inputs)
    {
        sdkInputs = inputs;
        if (context == null)
        {
            factory = Http.CreateFactory(true, Database.ScanId);
            context = await factory.CreateContextAsync(DiscoveryTransportFixture.WebUrl, default);
            BootstrapRequestCount = Http.Transport.Requests.Count;
        }
        Http.Transport.Respond = request =>
        {
            var uri = request.RequestUri;
            if (uri.AbsoluteUri.Contains("getfileby", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = Uri.UnescapeDataString(uri.AbsoluteUri);
                var input = sdkInputs.Single(value => decoded.Contains(value.Path, StringComparison.Ordinal));
                var metadata = new Dictionary<string, object>
                {
                    ["Name"] = input.SourceName ?? System.IO.Path.GetFileName(input.SourcePath ?? input.Path),
                    ["ServerRelativeUrl"] = input.SourcePath ?? input.Path,
                    ["ListId"] = input.Item.HasValue ? ListId : Guid.Empty,
                };
                if (input.ReturnUniqueId) metadata["UniqueId"] = input.SourceId ?? input.FileId;
                if (input.ReturnVersionAndLength)
                {
                    metadata["Length"] = input.Bytes.LongLength;
                    metadata["ETag"] = input.ETag;
                    metadata["MajorVersion"] = input.Major;
                    metadata["MinorVersion"] = input.Minor;
                }
                return DiscoveryTransportFixture.RecordingTransport.Json(JsonSerializer.Serialize(metadata));
            }
            if (uri.AbsolutePath.EndsWith("/download.aspx", StringComparison.Ordinal))
            {
                var input = sdkInputs.Single(value => uri.Query.Contains(
                    (value.SourceId ?? value.FileId).ToString("D"), StringComparison.OrdinalIgnoreCase));
                return new HttpResponseMessage(input.DownloadStatus) { Content = new ByteArrayContent(input.Bytes) };
            }
            throw new InvalidOperationException("Unexpected offline acquisition request: " + uri);
        };
    }

    internal Task<AspxSourceReadResult> ReadWithSdkAsync(ClassicPageDiscovery row, CancellationToken token) =>
        PnPAspxSourceReader.ReadAsync(context, SiteId, WebId, row, token);

    internal async Task RunAsync(IEnumerable<RawDiscoveryRecord> records,
        Func<ClassicPageDiscovery, CancellationToken, Task<AspxSourceReadResult>> reader,
        Func<ClassicPageDiscovery, CancellationToken, Task> afterAcquisition = null)
    {
        var acquire = AspxSourceAcquisition.ForAssessmentScan(Scan, async (row, token) =>
        {
            var stored = await Database.ReopenAsync(row.RecordKey);
            stored.DiscoveryStatus.Should().Be("Discovered", "existence must be committed before enrichment");
            stored.ReadSourceEvidence().DiscoveryObservations.Should().Contain(value =>
                value.DiscoveryRecord.SourceObjectId == row.DiscoveryObservation.DiscoveryRecord.SourceObjectId);
            row.AssessmentStatus.Should().BeNull("source acquisition precedes ListItem/body admission");
            AcquisitionCalls.Add(row.FileName);
            AcquiredRows.Add(row);
            return await reader(row, token);
        });
        using var provider = new SyntheticPageInheritsProvider(Site, records);
        await new AssessmentWebDiscovery(Database.ScanId, Site, Web, Writer, async (row, token) =>
        {
            await acquire(row, token);
            if (afterAcquisition != null) await afterAcquisition(row, token);
        }, 1).RunAsync(provider, default);
        (await Writer.ReadWebCoverageAsync(Database.ScanId, Site, Web)).Should().Be("Complete",
            "source/parser failures cannot change successful enumeration into a Web skip");
    }

    public void Dispose()
    {
        context?.Dispose();
        factory?.Dispose();
        Http.Dispose();
        Database.Dispose();
    }
}

/// <summary>Replays raw file, Form and View surfaces into the existing assessment entry path.</summary>
internal sealed class SyntheticPageInheritsProvider : IAspxDiscoveryProvider
{
    private readonly RawDiscoveryRecord[] records;
    private readonly DiscoveryScopeRegistration[] surfaces;
    public DiscoveryScopeRegistration RootScope { get; }

    internal SyntheticPageInheritsProvider(string site, IEnumerable<RawDiscoveryRecord> records)
    {
        this.records = records.ToArray();
        RootScope = new("synthetic-web", null, DiscoveryScopeKind.Web, null, site, "offline-synthetic");
        surfaces = new[]
        {
            new DiscoveryScopeRegistration("synthetic-raw", RootScope.ScopeKey, DiscoveryScopeKind.Folder,
                DiscoverySourceKind.RawListLibraryFiles, "/synthetic/raw", "offline-synthetic"),
            new DiscoveryScopeRegistration("synthetic-forms", RootScope.ScopeKey, DiscoveryScopeKind.Folder,
                DiscoverySourceKind.ListFormBackingFiles, "/synthetic/forms", "offline-synthetic"),
            new DiscoveryScopeRegistration("synthetic-views", RootScope.ScopeKey, DiscoveryScopeKind.Folder,
                DiscoverySourceKind.ListViewBackingFiles, "/synthetic/views", "offline-synthetic"),
            new DiscoveryScopeRegistration("synthetic-excluded", RootScope.ScopeKey, DiscoveryScopeKind.Folder,
                DiscoverySourceKind.RawListLibraryFiles, "/sites/outside/excluded", "offline-synthetic", Required: false),
        };
    }

    public Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(DiscoveryScopeRegistration parent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var children = parent == RootScope ? surfaces : Array.Empty<DiscoveryScopeRegistration>();
        return Task.FromResult(new DiscoveryChildEnumerationResult(DiscoveryScopeKind.Folder,
            children.Select(value => new DiscoveryChildExpectation(value.ScopeKey, value.Kind, value.SourceKind,
                value.Locator, value.PermissionContext, value.Required)).ToArray(),
            children, DiscoveryTerminalOutcome.Complete, "offline-synthetic"));
    }

    public IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface)
    {
        surface.Required.Should().BeTrue("policy-excluded scopes must never be acquired");
        return new Batch(records.Where(record => SourceKind(record.PhysicalLocator) == surface.SourceKind).ToArray());
    }

    private static DiscoverySourceKind SourceKind(string path) =>
        path?.Contains("/Forms/", StringComparison.OrdinalIgnoreCase) == true ? DiscoverySourceKind.ListFormBackingFiles :
        path?.Contains("/Views/", StringComparison.OrdinalIgnoreCase) == true ? DiscoverySourceKind.ListViewBackingFiles :
        DiscoverySourceKind.RawListLibraryFiles;

    private sealed class Batch(RawDiscoveryRecord[] records) : IRawDiscoverySource
    {
        public async IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return new(1, "synthetic-discovery-request", "synthetic-discovery-response", records, true,
                DiscoveryTerminalOutcome.Complete);
        }
    }

    public void Dispose() { }
}
