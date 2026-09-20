using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Net;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class SharePointLiveAspxDiscoveryRegressionTests : IClassFixture<ScanContextFixture>
{
    private readonly ScanContextFixture database;

    public SharePointLiveAspxDiscoveryRegressionTests(ScanContextFixture database) => this.database = database;

    [Fact]
    public async Task Web_root_records_retain_known_site_and_web_identity()
    {
        var siteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var webId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var siteUrl = new Uri("https://contoso.sharepoint.com/sites/a");
        using var factory = new RootTraversalFactory(siteUrl);
        using var provider = Provider(factory, siteUrl, siteId, webId);

        var webRootFolder = await WebRootFolderAsync(provider);
        var records = await ReadRecordsAsync(provider.CreateRawSource(webRootFolder));

        records.Should().ContainSingle(record => record.PhysicalLocator == "/sites/a/default.aspx");
        var rootPage = records.Single(record => record.PhysicalLocator == "/sites/a/default.aspx");
        rootPage.SiteCollectionId.Should().Be(siteId);
        rootPage.WebId.Should().Be(webId);
        rootPage.SiteUrl.Should().Be(siteUrl.AbsoluteUri);
        rootPage.WebUrl.Should().Be(siteUrl.AbsoluteUri);
    }

    [Fact]
    public async Task Descendant_web_root_fallback_targets_and_encodes_the_descendant_folder()
    {
        var siteUrl = new Uri("https://contoso.sharepoint.com/sites/a");
        using var factory = new RootTraversalFactory(siteUrl);
        using var provider = Provider(factory, siteUrl, Guid.NewGuid(), Guid.NewGuid());
        var webRootFolder = await WebRootFolderAsync(provider);
        var child = (await provider.EnumerateChildrenAsync(webRootFolder)).ObservedChildren.Single();

        await ReadRecordsAsync(provider.CreateRawSource(child));

        var request = factory.Client.Requests.Single(uri =>
            uri.AbsolutePath.Contains("GetFolderByServerRelativePath", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.EndsWith("/Files", StringComparison.OrdinalIgnoreCase));
        request.AbsolutePath.Contains("/RootFolder/", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        request.Fragment.Should().BeEmpty("# is part of the decoded SharePoint folder name, not a URI fragment");
        Uri.UnescapeDataString(request.Query).Should().Contain("/sites/a/Docs/A#B");
    }

    [Fact]
    public async Task Later_sparse_observation_does_not_erase_known_file_ownership()
    {
        var scanId = Guid.NewGuid();
        var siteUrl = "https://contoso.sharepoint.com/sites/a";
        var webUrl = "/";
        var richScope = new DiscoveryScopeRegistration("rich", null, DiscoveryScopeKind.Folder,
            DiscoverySourceKind.RawListLibraryFiles, "/docs", "test");
        var sparseScope = new DiscoveryScopeRegistration("sparse", null, DiscoveryScopeKind.Folder,
            DiscoverySourceKind.ListViewBackingFiles, "/view", "test");
        var siteId = Guid.NewGuid();
        var webId = Guid.NewGuid();
        var listId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var rich = new RawDiscoveryRecord(fileId.ToString("D"), fileId.ToString("D"), listId.ToString("D"),
            "page.aspx", "/sites/a/docs/page.aspx", true, "test", SiteCollectionId: siteId,
            WebId: webId, ListId: listId, FolderUniqueId: folderId, ListItemId: 7,
            ContentTypeId: "0x0101", PageType: "WikiPage", LibraryHidden: false);
        var sparse = rich with
        {
            ListId = null,
            FolderUniqueId = null,
            ListItemId = null,
            ContentTypeId = null,
            PageType = null,
            LibraryHidden = null,
        };
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        await writer.WriteAsync(new[] { AssessmentWebDiscovery.Page(scanId, siteUrl, webUrl, richScope, rich) });
        await writer.WriteAsync(new[] { AssessmentWebDiscovery.Page(scanId, siteUrl, webUrl, sparseScope, sparse) });

        using var read = database.CreateContext();
        var row = read.ClassicPageDiscoveries.Single(value => value.ScanId == scanId && value.RowType == "Page");
        row.ListId.Should().Be(listId);
        row.FolderUniqueId.Should().Be(folderId);
        row.ListItemId.Should().Be(7);
        row.ContentTypeId.Should().Be("0x0101");
        row.PageType.Should().Be("WikiPage");
        row.LibraryHidden.Should().BeFalse();
    }

    [Fact]
    public void Assessment_home_page_state_falls_back_without_erasing_a_known_value()
    {
        PageScanComponent.ResolveHomePageState("/sites/a/SitePages/Home.aspx", "SitePages/Home.aspx",
            welcomePageKnown: true, discoveredHomePage: false).Should().BeTrue();
        PageScanComponent.ResolveHomePageState("/sites/a/default.aspx", null,
            welcomePageKnown: false, discoveredHomePage: null).Should().BeNull();

        var page = new ClassicPage { HomePage = true };
        PageScanComponent.ApplyDiscoveryState(page, new ClassicPageDiscovery { HomePage = null });
        page.HomePage.Should().BeTrue();
    }

    private static SharePointLiveAspxDiscoveryProvider Provider(RootTraversalFactory factory, Uri siteUrl,
        Guid siteId, Guid webId) => new(
        new SharePointLiveAspxDiscoveryOptions("test", "declared", "revision", new string('a', 64)),
        factory, new AspxWebAcquisitionContext(siteId, siteUrl, webId, siteUrl, "/sites/a", "STS#3"));

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

    private sealed class RootTraversalFactory : ISharePointAspxRestClientFactory
    {
        internal RootTraversalFactory(Uri webUrl) => Client = new RootTraversalClient(webUrl);
        internal RootTraversalClient Client { get; }
        public Task<ISharePointAspxRestClient> GetAsync(Uri webUrl,
            CancellationToken cancellationToken = default) => Task.FromResult<ISharePointAspxRestClient>(Client);
        public void Dispose() => Client.Dispose();
    }

    private sealed class RootTraversalClient : ISharePointAspxRestClient
    {
        internal RootTraversalClient(Uri webUrl) => WebUrl = webUrl;
        internal List<Uri> Requests { get; } = new();
        public Uri WebUrl { get; }

        public Task<SharePointRestPage> GetPageAsync(Uri requestUri,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(requestUri);
            return Task.FromResult(new SharePointRestPage(requestUri, HttpStatusCode.OK,
                Array.Empty<JsonElement>(), null, new string('b', 64), "odata-nometadata",
                DiscoveryTerminalOutcome.Complete));
        }

        public Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SharePointModeledValue> ReadWelcomePageAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(new SharePointModeledValue(
            DiscoveryTerminalOutcome.Empty, null, "fake", "welcome", null, "fake:welcome",
            DateTimeOffset.UtcNow));

        public Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl,
            CancellationToken cancellationToken = default)
        {
            if (serverRelativeUrl == "/sites/a")
                return Task.FromResult(new SharePointModeledFolderResult(
                    DiscoveryTerminalOutcome.Complete, Guid.NewGuid().ToString("D"), serverRelativeUrl,
                    new[]
                    {
                        new SharePointModeledFolder(Guid.NewGuid().ToString("D"), "A#B",
                            "/sites/a/Docs/A#B"),
                    },
                    new[]
                    {
                        new SharePointModeledFile(Guid.NewGuid().ToString("D"), "default.aspx",
                            "/sites/a/default.aspx", "Uncustomized"),
                    },
                    "fake", "folder", null, "fake:folder", DateTimeOffset.UtcNow));

            return Task.FromResult(new SharePointModeledFolderResult(
                DiscoveryTerminalOutcome.Failed, null, serverRelativeUrl,
                Array.Empty<SharePointModeledFolder>(), Array.Empty<SharePointModeledFile>(),
                "fake", "folder", "failed", "fake:folder", DateTimeOffset.UtcNow));
        }

        public void Dispose() { }
    }
}
