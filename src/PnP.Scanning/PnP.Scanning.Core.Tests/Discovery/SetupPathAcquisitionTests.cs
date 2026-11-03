using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Net;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

/// <summary>
/// Acquisition of the file <c>vti_setuppath</c> value. It rides along on the existing folder-files request,
/// so no page costs an extra round trip, and a later sparse observation must not erase it.
/// </summary>
[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class SetupPathAcquisitionTests : IClassFixture<ScanContextFixture>
{
    private static readonly Uri SiteUrl = new("https://contoso.sharepoint.com/sites/a");
    private readonly ScanContextFixture database;

    public SetupPathAcquisitionTests(ScanContextFixture database) => this.database = database;

    /// <summary>Keeps the setup-path property bag in every bounded data and terminal-empty request.</summary>
    [Fact]
    public async Task Rest_folder_files_request_asks_for_the_file_property_bag()
    {
        using var factory = new SetupPathFactory();
        using var provider = Provider(factory);

        await ReadRecordsAsync(provider.CreateRawSource(await LibraryFolderAsync(provider)));

        var requests = factory.Client.Requests.Where(uri =>
            uri.AbsolutePath.EndsWith("/Files", StringComparison.OrdinalIgnoreCase)).ToArray();
        requests.Should().HaveCount(2, "one data page and one terminal-empty page retain the same projection");
        foreach (var request in requests)
        {
            var query = Uri.UnescapeDataString(request.Query);
            query.Should().Contain("$expand=ListItemAllFields,Properties");
            query.Should().Contain("Properties");
        }
    }

    [Fact]
    public async Task Rest_folder_files_read_the_setup_path_from_the_nested_property_bag()
    {
        using var factory = new SetupPathFactory();
        using var provider = Provider(factory);

        var records = await ReadRecordsAsync(provider.CreateRawSource(await LibraryFolderAsync(provider)));

        records.Single(record => record.FileName == "smr-research-gallery.aspx").SetupPath
            .Should().Be(@"1033\STS\doctemp\smartpgs\spstd3.aspx");
        records.Single(record => record.FileName == "wiki.aspx").SetupPath
            .Should().Be(@"DocumentTemplates\wkpstd.aspx");
        // An unghosted file carries no setup-path property at all.
        records.Single(record => record.FileName == "uploaded.aspx").SetupPath.Should().BeNull();
    }

    /// <summary>Retains the root file setup path through REST even when an SDK model contains children.</summary>
    [Fact]
    public async Task Bounded_web_root_files_carry_the_setup_path_through_the_raw_record()
    {
        using var factory = new SetupPathFactory();
        using var provider = Provider(factory);

        var records = await ReadRecordsAsync(provider.CreateRawSource(await WebRootFolderAsync(provider)));

        records.Single().SetupPath.Should().Be(@"Features\SitePagePublishing\Home.aspx");
    }

    [Fact]
    public async Task Later_sparse_observation_does_not_erase_a_known_setup_path()
    {
        var scanId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var richScope = new DiscoveryScopeRegistration("rich", null, DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles, "/docs", "test");
        var sparseScope = new DiscoveryScopeRegistration("sparse", null, DiscoveryScopeKind.Folder,
            DiscoverySourceKind.ListViewBackingFiles, "/view", "test");
        var rich = new RawDiscoveryRecord(fileId.ToString("D"), fileId.ToString("D"), "list",
            "page.aspx", "/sites/a/docs/page.aspx", true, "test",
            SetupPath: @"pages\viewpage.aspx");
        var sparse = rich with { SetupPath = null };
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);

        await writer.WriteAsync(new[] { AssessmentWebDiscovery.Page(scanId, SiteUrl.AbsoluteUri, "/", richScope, rich) });
        await writer.WriteAsync(new[] { AssessmentWebDiscovery.Page(scanId, SiteUrl.AbsoluteUri, "/", sparseScope, sparse) });

        using var read = database.CreateContext();
        read.ClassicPageDiscoveries.Single(row => row.ScanId == scanId && row.RowType == "Page")
            .SetupPath.Should().Be(@"pages\viewpage.aspx");
    }

    private static SharePointLiveAspxDiscoveryProvider Provider(SetupPathFactory factory) => new(
        new SharePointLiveAspxDiscoveryOptions("test", "declared", "revision", new string('a', 64)),
        factory, new AspxWebAcquisitionContext(Guid.NewGuid(), SiteUrl, Guid.NewGuid(), SiteUrl,
            "/sites/a", "STS#3"));

    private static async Task<DiscoveryScopeRegistration> LibraryFolderAsync(
        SharePointLiveAspxDiscoveryProvider provider)
    {
        var containers = (await provider.EnumerateChildrenAsync(provider.RootScope)).ObservedChildren;
        var list = containers.Single(scope => scope.Metadata?["role"] == "list");
        return (await provider.EnumerateChildrenAsync(list)).ObservedChildren
            .Single(scope => scope.Metadata?["role"] == "folder");
    }

    private static async Task<DiscoveryScopeRegistration> WebRootFolderAsync(
        SharePointLiveAspxDiscoveryProvider provider)
    {
        var containers = (await provider.EnumerateChildrenAsync(provider.RootScope)).ObservedChildren;
        var webRoot = containers.Single(scope => scope.Metadata?["role"] == "web-root");
        return (await provider.EnumerateChildrenAsync(webRoot)).ObservedChildren.Single();
    }

    private static async Task<List<RawDiscoveryRecord>> ReadRecordsAsync(IRawDiscoverySource source)
    {
        var records = new List<RawDiscoveryRecord>();
        await foreach (var batch in source.ReadBatchesAsync()) records.AddRange(batch.Records);
        return records;
    }

    private sealed class SetupPathFactory : ISharePointAspxRestClientFactory
    {
        internal SetupPathClient Client { get; } = new();
        public Task<ISharePointAspxRestClient> GetAsync(Uri webUrl,
            CancellationToken cancellationToken = default) => Task.FromResult<ISharePointAspxRestClient>(Client);
        public void Dispose() => Client.Dispose();
    }

    private sealed class SetupPathClient : ISharePointAspxRestClient
    {
        private const string ListsJson = """
            [{
              "Id": "33333333-3333-3333-3333-333333333333",
              "Title": "Documents",
              "BaseType": 1,
              "BaseTemplate": 101,
              "Hidden": false,
              "IsCatalog": false,
              "DefaultViewUrl": "/sites/a/Shared Documents/Forms/AllItems.aspx",
              "RootFolder": {
                "ServerRelativeUrl": "/sites/a/Shared Documents",
                "UniqueId": "44444444-4444-4444-4444-444444444444"
              }
            }]
            """;

        // Shaped like a real $select/$expand=Properties response: REST escapes the '_' of the property
        // bag name, and an unghosted file simply has no setup-path entry.
        private const string FilesJson = """
            [{
              "UniqueId": "55555555-5555-5555-5555-555555555555",
              "Name": "smr-research-gallery.aspx",
              "ServerRelativeUrl": "/sites/a/Shared Documents/smr-research-gallery.aspx",
              "CustomizedPageStatus": "Uncustomized",
              "Length": "1234",
              "ListItemAllFields": { "Id": 5, "ContentTypeId": "0x01010901" },
              "Properties": { "vti_x005f_setuppath": "1033\\STS\\doctemp\\smartpgs\\spstd3.aspx" }
            },{
              "UniqueId": "66666666-6666-6666-6666-666666666666",
              "Name": "wiki.aspx",
              "ServerRelativeUrl": "/sites/a/Shared Documents/wiki.aspx",
              "CustomizedPageStatus": "Customized",
              "Length": "2345",
              "ListItemAllFields": { "Id": 6, "ContentTypeId": "0x010108" },
              "Properties": { "vti_x005f_setuppath": "DocumentTemplates\\wkpstd.aspx" }
            },{
              "UniqueId": "77777777-7777-7777-7777-777777777777",
              "Name": "uploaded.aspx",
              "ServerRelativeUrl": "/sites/a/Shared Documents/uploaded.aspx",
              "CustomizedPageStatus": "Customized",
              "Length": "3456",
              "ListItemAllFields": { "Id": 7, "ContentTypeId": "0x010109" },
              "Properties": {}
            }]
            """;

        internal List<Uri> Requests { get; } = new();
        public Uri WebUrl => SiteUrl;

        /// <summary>Returns folder-specific setup-path metadata and slices Files using the actual paging controls.</summary>
        public Task<SharePointRestPage> GetPageAsync(Uri requestUri,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(requestUri);
            var files = requestUri.AbsolutePath.EndsWith("/Files", StringComparison.OrdinalIgnoreCase);
            var root = requestUri.AbsolutePath.EndsWith("/RootFolder/Files", StringComparison.OrdinalIgnoreCase);
            var rootJson = JsonSerializer.Serialize(new[] { new
            {
                UniqueId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
                Name = "Home.aspx", ServerRelativeUrl = "/sites/a/Home.aspx", CustomizedPageStatus = "Customized",
                Properties = new Dictionary<string, string> { ["vti_x005f_setuppath"] = @"Features\SitePagePublishing\Home.aspx" }
            }});
            var json = files ? root ? rootJson : FilesJson
                : requestUri.AbsolutePath.EndsWith("/lists", StringComparison.OrdinalIgnoreCase) ? ListsJson : "[]";
            var items = Items(json);
            if (files)
            {
                var query = requestUri.Query.TrimStart('?').Split('&').Select(segment => segment.Split('=', 2))
                    .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "");
                int offset = query.TryGetValue("$skip", out var skip) ? int.Parse(skip) : 0;
                int size = query.TryGetValue("$top", out var top) ? int.Parse(top) : int.MaxValue;
                items = items.Skip(offset).Take(size).ToArray();
            }
            return Task.FromResult(new SharePointRestPage(requestUri, HttpStatusCode.OK, items, null,
                new string('b', 64), "odata-nometadata", DiscoveryTerminalOutcome.Complete));
        }

        public Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SharePointModeledValue> ReadWelcomePageAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new SharePointModeledValue(
            DiscoveryTerminalOutcome.Empty, null, "fake", "welcome", null, "fake:welcome",
            DateTimeOffset.UtcNow));

        public Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharePointModeledFolderResult(DiscoveryTerminalOutcome.Complete,
                "88888888-8888-8888-8888-888888888888", serverRelativeUrl,
                Array.Empty<SharePointModeledFolder>(),
                new[]
                {
                    new SharePointModeledFile("99999999-9999-9999-9999-999999999999", "Home.aspx",
                        "/sites/a/Home.aspx", "Customized", @"Features\SitePagePublishing\Home.aspx"),
                },
                "fake", "folder", null, "fake:folder", DateTimeOffset.UtcNow));

        public void Dispose() { }

        private static IReadOnlyList<JsonElement> Items(string json) =>
            JsonDocument.Parse(json).RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }
}
