using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using System.Net;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

/// <summary>
/// Acquisition of pages that own no file of their own: a list item addressed through a shared display
/// form as "&lt;displayForm&gt;?ID=&lt;n&gt;". One form file backs N such pages, so they are read from
/// the item stream rather than from a physical file enumeration, as a surface of the owning list.
/// </summary>
[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class DisplayFormDiscoveryTests
{
    private static readonly Uri SiteUrl = new("https://contoso.sharepoint.com/sites/a");

    [Fact]
    public async Task All_lists_request_carries_both_display_form_tiers()
    {
        // Both tiers ride along on the existing all-lists request rather than costing a round trip per
        // list. The content type tier is the one that in practice ever names a content page form.
        using var factory = new DisplayFormFactory();
        using var provider = Provider(factory);

        await provider.EnumerateChildrenAsync(provider.RootScope);

        var request = factory.Client.Requests.Single(uri =>
            uri.AbsolutePath.EndsWith("/lists", StringComparison.OrdinalIgnoreCase));
        var query = Uri.UnescapeDataString(request.Query);
        query.Should().Contain("DefaultDisplayFormUrl");
        query.Should().Contain("ContentTypes/DisplayFormUrl");
        query.Should().Contain("$expand=RootFolder,ContentTypes");
    }

    [Fact]
    public async Task List_with_a_content_page_form_exposes_a_display_form_surface()
    {
        using var factory = new DisplayFormFactory();
        using var provider = Provider(factory);

        var surface = await DisplayFormSurfaceAsync(provider, "Posts");

        surface.Should().NotBeNull("the blog Posts list resolves a content page form");
        surface.SourceKind.Should().Be(DiscoverySourceKind.ListItemDisplayFormPages);
    }

    [Theory]
    // A document library's items are files, already inventoried with a real FileUniqueId.
    [InlineData("Documents")]
    // Ordinary data rows viewed through the stock form: enumerating these would report every list item
    // in the tenant as a page.
    [InlineData("Announcements")]
    public async Task Lists_without_content_page_forms_expose_no_display_form_surface(string title)
    {
        using var factory = new DisplayFormFactory();
        using var provider = Provider(factory);

        (await DisplayFormSurfaceAsync(provider, title)).Should().BeNull();
    }

    [Fact]
    public async Task Items_become_pages_addressed_through_their_resolved_form()
    {
        using var factory = new DisplayFormFactory();
        using var provider = Provider(factory);
        var surface = await DisplayFormSurfaceAsync(provider, "Posts");

        var records = await ReadRecordsAsync(provider.CreateRawSource(surface));

        // Item 7 carries the Post content type, item 9 only the list default (the stock form).
        records.Select(record => record.PhysicalLocator).Should()
            .BeEquivalentTo("/sites/a/Lists/Posts/Post.aspx?ID=7");
        var post = records.Single();
        post.ListItemId.Should().Be(7);
        post.ListId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        post.FileUniqueId.Should().BeNull("the page owns no file; the form file is not this page");
        post.HomePage.Should().BeFalse("a display form is never the web's welcome page");
        post.LocatorIsGuaranteedPhysicalFilePath.Should().BeFalse();
    }

    [Fact]
    public async Task A_display_form_page_is_admitted_as_aspx()
    {
        // Admission matches on the leaf name, so the record must carry the form's file name rather than
        // the addressed URL, which ends in a query string.
        using var factory = new DisplayFormFactory();
        using var provider = Provider(factory);
        var surface = await DisplayFormSurfaceAsync(provider, "Posts");

        var record = (await ReadRecordsAsync(provider.CreateRawSource(surface))).Single();

        record.FileName.Should().Be("Post.aspx");
        AspxAdmission.Evaluate(record).IsAspx.Should().BeTrue();
    }

    [Fact]
    public async Task A_display_form_page_is_recorded_as_a_list_item_not_a_file()
    {
        // "File" asserts the row represents a file. One form file backs N of these addresses, so the
        // page is a ListItem — and the page scanner routes on exactly that, because such a row cannot
        // be read as a physical file: the item's FileRef is not the form's URL.
        using var factory = new DisplayFormFactory();
        using var provider = Provider(factory);
        var surface = await DisplayFormSurfaceAsync(provider, "Posts");
        var record = (await ReadRecordsAsync(provider.CreateRawSource(surface))).Single();

        var row = AssessmentWebDiscovery.Page(Guid.NewGuid(), SiteUrl.AbsoluteUri, "/", surface, record);

        row.ScopeType.Should().Be("ListItem");
        Core.Scanners.PageScanComponent.IsDisplayFormPage(row).Should().BeTrue();
        row.Url.Should().Be("/sites/a/Lists/Posts/Post.aspx?ID=7");
        row.ListItemId.Should().Be(7);
    }

    [Fact]
    public async Task The_assessment_loop_must_route_a_display_form_page_before_classifying_its_list()
    {
        // Ordering guard. A display-form address lives inside its list's root folder, so the
        // file-level route's containment check would read its null-free identity as a list-backed
        // file whose association went unobserved, and fail it. The loop must therefore recognise the
        // row as a display-form page before it reaches ClassifyListAssociation.
        using var factory = new DisplayFormFactory();
        using var provider = Provider(factory);
        var surface = await DisplayFormSurfaceAsync(provider, "Posts");
        var record = (await ReadRecordsAsync(provider.CreateRawSource(surface))).Single();
        var row = AssessmentWebDiscovery.Page(Guid.NewGuid(), SiteUrl.AbsoluteUri, "/", surface, record);

        Core.Scanners.PageScanComponent.IsDisplayFormPage(row)
            .Should().BeTrue("the loop skips on this before any list classification runs");

        // What the file-level route would have concluded had the guard not run first.
        Core.Scanners.PageScanComponent.ClassifyListAssociation(row, listMatched: false,
                new[] { "/sites/a/Lists/Posts" })
            .Should().Be(Core.Scanners.PageScanComponent.ListAssociationState.OwningListUnresolved,
                "which AddFileLevelPage records as Failed - the verdict the guard exists to prevent");
    }

    [Fact]
    public async Task A_physically_discovered_page_stays_a_file()
    {
        using var factory = new DisplayFormFactory();
        using var provider = Provider(factory);
        var scope = (await provider.EnumerateChildrenAsync(provider.RootScope)).ObservedChildren.First();
        var file = new RawDiscoveryRecord(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            "container", "default.aspx", "/sites/a/default.aspx", true, "test");

        var row = AssessmentWebDiscovery.Page(Guid.NewGuid(), SiteUrl.AbsoluteUri, "/", scope, file);

        row.ScopeType.Should().Be("File");
        Core.Scanners.PageScanComponent.IsDisplayFormPage(row).Should().BeFalse();
    }

    private static async Task<DiscoveryScopeRegistration> DisplayFormSurfaceAsync(
        SharePointLiveAspxDiscoveryProvider provider, string listTitle)
    {
        var containers = (await provider.EnumerateChildrenAsync(provider.RootScope)).ObservedChildren;
        var list = containers.Single(scope => scope.Metadata?.GetValueOrDefault("title") == listTitle);
        return (await provider.EnumerateChildrenAsync(list)).ObservedChildren
            .SingleOrDefault(scope => scope.SourceKind == DiscoverySourceKind.ListItemDisplayFormPages);
    }

    private static SharePointLiveAspxDiscoveryProvider Provider(DisplayFormFactory factory) =>
        new(new SharePointLiveAspxDiscoveryOptions("test", "declared", "revision", new string('a', 64)),
            factory, new AspxWebAcquisitionContext(Guid.NewGuid(), SiteUrl, Guid.NewGuid(), SiteUrl,
                "/sites/a", "STS#3"));

    private static async Task<List<RawDiscoveryRecord>> ReadRecordsAsync(IRawDiscoverySource source)
    {
        var records = new List<RawDiscoveryRecord>();
        await foreach (var batch in source.ReadBatchesAsync()) records.AddRange(batch.Records);
        return records;
    }

    private sealed class DisplayFormFactory : ISharePointAspxRestClientFactory
    {
        internal DisplayFormClient Client { get; } = new();
        public Task<ISharePointAspxRestClient> GetAsync(Uri webUrl,
            CancellationToken cancellationToken = default) => Task.FromResult<ISharePointAspxRestClient>(Client);
        public void Dispose() => Client.Dispose();
    }

    private sealed class DisplayFormClient : ISharePointAspxRestClient
    {
        // The literal form URLs are the shapes a live tenant returns: a content type's DisplayFormUrl
        // comes back web-relative exactly as the definition wrote it, while a list's
        // DefaultDisplayFormUrl is already server-relative and names DispForm.aspx for all but a
        // handful of templates.
        private const string ListsJson = """
            [{
              "Id": "11111111-1111-1111-1111-111111111111",
              "Title": "Documents",
              "BaseType": 1,
              "BaseTemplate": 101,
              "Hidden": false,
              "IsCatalog": false,
              "DefaultDisplayFormUrl": "/sites/a/Shared Documents/Forms/DispForm.aspx",
              "ContentTypes": [{ "Id": "0x0101", "DisplayFormUrl": "Lists/Posts/Post.aspx" }],
              "RootFolder": { "ServerRelativeUrl": "/sites/a/Shared Documents", "UniqueId": "aaaaaaaa-1111-1111-1111-111111111111" }
            },{
              "Id": "22222222-2222-2222-2222-222222222222",
              "Title": "Posts",
              "BaseType": 0,
              "BaseTemplate": 301,
              "Hidden": false,
              "IsCatalog": false,
              "DefaultDisplayFormUrl": "/sites/a/Lists/Posts/DispForm.aspx",
              "ContentTypes": [{ "Id": "0x0110", "DisplayFormUrl": "Lists/Posts/Post.aspx" }],
              "RootFolder": { "ServerRelativeUrl": "/sites/a/Lists/Posts", "UniqueId": "aaaaaaaa-2222-2222-2222-222222222222" }
            },{
              "Id": "33333333-3333-3333-3333-333333333333",
              "Title": "Announcements",
              "BaseType": 0,
              "BaseTemplate": 104,
              "Hidden": false,
              "IsCatalog": false,
              "DefaultDisplayFormUrl": "/sites/a/Lists/Announcements/DispForm.aspx",
              "ContentTypes": [{ "Id": "0x0104", "DisplayFormUrl": "" }],
              "RootFolder": { "ServerRelativeUrl": "/sites/a/Lists/Announcements", "UniqueId": "aaaaaaaa-3333-3333-3333-333333333333" }
            }]
            """;

        private const string ItemsJson = """
            [{ "Id": 7, "ContentTypeId": "0x0110007C4E8A" },
             { "Id": 9, "ContentTypeId": "0x0100ABCDEF" }]
            """;

        internal List<Uri> Requests { get; } = new();
        public Uri WebUrl => SiteUrl;

        public Task<SharePointRestPage> GetPageAsync(Uri requestUri,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(requestUri);
            var json = requestUri.AbsolutePath.EndsWith("/items", StringComparison.OrdinalIgnoreCase) ? ItemsJson
                : requestUri.AbsolutePath.EndsWith("/lists", StringComparison.OrdinalIgnoreCase) ? ListsJson
                : "[]";
            return Task.FromResult(new SharePointRestPage(requestUri, HttpStatusCode.OK, Items(json), null,
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
                "cccccccc-cccc-cccc-cccc-cccccccccccc", serverRelativeUrl,
                Array.Empty<SharePointModeledFolder>(), Array.Empty<SharePointModeledFile>(),
                "fake", "folder", null, "fake:folder", DateTimeOffset.UtcNow));

        public void Dispose() { }

        private static IReadOnlyList<JsonElement> Items(string json) =>
            JsonDocument.Parse(json).RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }
}
