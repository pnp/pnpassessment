using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

/// <summary>
/// Captures declared list and registered content-type display forms without enumerating list items.
/// </summary>
[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class DisplayFormDiscoveryTests
{
    private static readonly Uri SiteUrl = new("https://contoso.sharepoint.com/sites/a");

    /// <summary>
    /// Requires the all-lists request to include list-default and content-type form declarations without querying item collections.
    /// </summary>
    [Fact]
    public async Task All_lists_request_carries_both_display_form_tiers_without_item_reads()
    {
        using var factory = new DisplayFormFactory();
        using var provider = Provider(factory);

        await provider.EnumerateChildrenAsync(provider.RootScope);

        var request = factory.Client.Requests.Single(uri =>
            uri.AbsolutePath.EndsWith("/lists", StringComparison.OrdinalIgnoreCase));
        var query = Uri.UnescapeDataString(request.Query);
        query.Should().Contain("DefaultDisplayFormUrl");
        query.Should().Contain("ContentTypes/DisplayFormUrl");
        query.Should().Contain("$expand=RootFolder,ContentTypes");
        factory.Client.Requests.Should().NotContain(uri => uri.AbsolutePath.EndsWith("/items", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Retains declared form locations and content-type scope as unlinked reference evidence, without deriving per-item pages or requests.
    /// </summary>
    [Fact]
    public async Task Registered_forms_are_evidence_without_creating_item_page_surfaces()
    {
        using var factory = new DisplayFormFactory();
        using var provider = Provider(factory);

        var lists = (await provider.EnumerateChildrenAsync(provider.RootScope)).ObservedChildren;
        var posts = lists.Single(scope => scope.Metadata?.GetValueOrDefault("title") == "Posts");
        var children = await provider.EnumerateChildrenAsync(posts);

        children.ObservedChildren.Should().NotContain(scope => scope.Locator.Contains("Items REST objects", StringComparison.Ordinal));
        var references = provider.ReferenceCollector.ReadReferenceEvidence();
        references.Count(value => value.SourceKind == AspxReferenceSourceKinds.ListDefaultDisplayForm).Should().Be(3);
        references.Count(value => value.SourceKind == AspxReferenceSourceKinds.ContentTypeDisplayForm).Should().Be(2);

        var postForm = references.Single(value => value.SourceKind == AspxReferenceSourceKinds.ContentTypeDisplayForm &&
            value.ListId == Guid.Parse("22222222-2222-2222-2222-222222222222"));
        postForm.ContentTypeId.Should().Be("0x0110");
        postForm.RawLocator.Should().Be("/sites/a/Lists/Posts/Post.aspx");
        postForm.Disposition.Should().Be(AspxReferenceDispositions.Declared);
        postForm.LinkedFileUniqueId.Should().BeNull();
        references.Should().NotContain(value => value.RawLocator != null &&
            value.RawLocator.Contains("?ID=", StringComparison.OrdinalIgnoreCase));
        factory.Client.Requests.Should().NotContain(uri => uri.AbsolutePath.EndsWith("/items", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Preserves content-type form declarations across verbose and no-metadata collection wrappers and both supported ID shapes.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Wrapped_and_flattened_content_type_ids_preserve_declarations(bool wrappedContentTypes, bool stringContentTypeIds)
    {
        using var factory = new DisplayFormFactory(wrappedContentTypes, stringContentTypeIds);
        using var provider = Provider(factory);

        await provider.EnumerateChildrenAsync(provider.RootScope);

        provider.ReferenceCollector.ReadReferenceEvidence().Should().Contain(value =>
            value.SourceKind == AspxReferenceSourceKinds.ContentTypeDisplayForm &&
            value.SourceObjectId.EndsWith(":ct:0x0110", StringComparison.Ordinal) &&
            value.RawLocator == "/sites/a/Lists/Posts/Post.aspx");
        factory.Client.Requests.Should().NotContain(uri => uri.AbsolutePath.EndsWith("/items", StringComparison.OrdinalIgnoreCase));
        factory.Client.ResponseSchemaFlavors.Should().OnlyContain(flavor =>
            flavor == (wrappedContentTypes ? "odata-verbose" : "odata-nometadata"));
    }

    /// <summary>
    /// Keeps each registered content type and its declared form path distinct instead of collapsing them into one list-level reference.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multiple_registered_content_type_forms_remain_separate_references(bool wrappedContentTypes)
    {
        const string forms = "[{ \"Id\": { \"StringValue\": \"0x0110\" }, \"DisplayFormUrl\": \"Lists/Posts/Post.aspx\" }, " +
            "{ \"Id\": { \"StringValue\": \"0x0110007C4E\" }, \"DisplayFormUrl\": \"Lists/Posts/Special.aspx\" }]";
        using var factory = new DisplayFormFactory(wrappedContentTypes, postContentTypesJson: forms);
        using var provider = Provider(factory);

        await provider.EnumerateChildrenAsync(provider.RootScope);

        var postReferences = provider.ReferenceCollector.ReadReferenceEvidence()
            .Where(value => value.SourceKind == AspxReferenceSourceKinds.ContentTypeDisplayForm &&
                value.ListId == Guid.Parse("22222222-2222-2222-2222-222222222222")).ToArray();
        postReferences.Select(value => value.ContentTypeId).Should().BeEquivalentTo("0x0110", "0x0110007C4E");
        postReferences.Select(value => value.RawLocator).Should().BeEquivalentTo(
            "/sites/a/Lists/Posts/Post.aspx", "/sites/a/Lists/Posts/Special.aspx");
    }

    private static SharePointLiveAspxDiscoveryProvider Provider(DisplayFormFactory factory) =>
        new(new SharePointLiveAspxDiscoveryOptions("test", "declared", "revision", new string('a', 64)),
            factory, new AspxWebAcquisitionContext(Guid.NewGuid(), SiteUrl, Guid.NewGuid(), SiteUrl,
                "/sites/a", "STS#3"));

    private sealed class DisplayFormFactory : ISharePointAspxRestClientFactory
    {
        internal DisplayFormClient Client { get; }

        internal DisplayFormFactory(bool wrappedContentTypes = false, bool stringContentTypeIds = false,
            string postContentTypesJson = null) =>
            Client = new DisplayFormClient(wrappedContentTypes, stringContentTypeIds, postContentTypesJson);

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
              "ContentTypes": [{ "Id": { "StringValue": "0x0101" }, "DisplayFormUrl": "Lists/Posts/Post.aspx" }],
              "RootFolder": { "ServerRelativeUrl": "/sites/a/Shared Documents", "UniqueId": "aaaaaaaa-1111-1111-1111-111111111111" }
            },{
              "Id": "22222222-2222-2222-2222-222222222222",
              "Title": "Posts",
              "BaseType": 0,
              "BaseTemplate": 301,
              "Hidden": false,
              "IsCatalog": false,
              "DefaultDisplayFormUrl": "/sites/a/Lists/Posts/DispForm.aspx",
              "ContentTypes": [{ "Id": { "StringValue": "0x0110" }, "DisplayFormUrl": "Lists/Posts/Post.aspx" }],
              "RootFolder": { "ServerRelativeUrl": "/sites/a/Lists/Posts", "UniqueId": "aaaaaaaa-2222-2222-2222-222222222222" }
            },{
              "Id": "33333333-3333-3333-3333-333333333333",
              "Title": "Announcements",
              "BaseType": 0,
              "BaseTemplate": 104,
              "Hidden": false,
              "IsCatalog": false,
              "DefaultDisplayFormUrl": "/sites/a/Lists/Announcements/DispForm.aspx",
              "ContentTypes": [{ "Id": { "StringValue": "0x0104" }, "DisplayFormUrl": "" }],
              "RootFolder": { "ServerRelativeUrl": "/sites/a/Lists/Announcements", "UniqueId": "aaaaaaaa-3333-3333-3333-333333333333" }
            }]
            """;

        private const string ItemsJson = """
            [{ "Id": 7, "ContentTypeId": "0x0110007C4E8A" },
             { "Id": 9, "ContentTypeId": "0x0100ABCDEF" }]
            """;

        private readonly bool wrappedContentTypes;
        private readonly bool stringContentTypeIds;
        private readonly string postContentTypesJson;

        internal DisplayFormClient(bool wrappedContentTypes, bool stringContentTypeIds, string postContentTypesJson)
        {
            this.wrappedContentTypes = wrappedContentTypes;
            this.stringContentTypeIds = stringContentTypeIds;
            this.postContentTypesJson = postContentTypesJson;
        }

        internal List<Uri> Requests { get; } = new();
        internal List<string> ResponseSchemaFlavors { get; } = new();
        public Uri WebUrl => SiteUrl;

        public Task<SharePointRestPage> GetPageAsync(Uri requestUri,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(requestUri);
            var items = requestUri.AbsolutePath.EndsWith("/lists", StringComparison.OrdinalIgnoreCase) ? ListItems()
                : Items(requestUri.AbsolutePath.EndsWith("/items", StringComparison.OrdinalIgnoreCase) ? ItemsJson : "[]");
            // Exercise the real response parser, not just a hand-built SharePointRestPage.
            var body = wrappedContentTypes
                ? JsonSerializer.SerializeToUtf8Bytes(new { d = new { results = items } })
                : JsonSerializer.SerializeToUtf8Bytes(new { value = items });
            var page = SharePointRestResponseParser.Parse(requestUri, HttpStatusCode.OK, body, "application/json");
            ResponseSchemaFlavors.Add(page.SchemaFlavor);
            return Task.FromResult(page);
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

        private IReadOnlyList<JsonElement> ListItems()
        {
            var lists = JsonNode.Parse(ListsJson).AsArray();
            foreach (var list in lists)
            {
                if (postContentTypesJson != null && list["Title"].GetValue<string>() == "Posts")
                    list["ContentTypes"] = JsonNode.Parse(postContentTypesJson);
                var contentTypes = list["ContentTypes"].AsArray();
                if (stringContentTypeIds)
                    foreach (var contentType in contentTypes)
                        contentType["Id"] = contentType["Id"]["StringValue"].GetValue<string>();
                if (wrappedContentTypes)
                    list["ContentTypes"] = new JsonObject { ["results"] = contentTypes.DeepClone() };
            }
            return Items(lists.ToJsonString());
        }

        private static IReadOnlyList<JsonElement> Items(string json) =>
            JsonDocument.Parse(json).RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }
}
