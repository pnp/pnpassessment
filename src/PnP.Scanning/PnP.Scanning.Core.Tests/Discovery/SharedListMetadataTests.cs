using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using System.Net;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class SharedListMetadataTests
{
    private static readonly Uri WebUrl = new("https://contoso.sharepoint.com/sites/a");
    private static readonly Guid ListId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FolderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FileId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset ReceivedAt = DateTimeOffset.Parse("2026-10-21T00:00:00Z");

    [Theory]
    [InlineData("{\"Lists\":[]}")]
    [InlineData("{\"d\":{\"Lists\":[]}}")]
    [InlineData("{\"d\":{\"Lists\":{\"results\":[],\"__count\":\"0\"}}}")]
    [InlineData("{\"Lists\":{\"value\":[],\"@odata.count\":0}}")]
    [InlineData("{\"value\":{\"Lists\":[]}}")]
    public void Explicit_complete_empty_array_is_a_valid_snapshot(string json)
    {
        var snapshot = SharePointListMetadataSnapshot.TryCreate(new Uri(WebUrl.AbsoluteUri + "/"), json, ReceivedAt);

        snapshot.Should().NotBeNull();
        snapshot.WebUrl.Should().Be(WebUrl);
        snapshot.Items.Should().BeEmpty();
        snapshot.ReceivedAtUtc.Should().Be(ReceivedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snapshot_supports_current_discovery_columns_even_when_the_collection_is_empty(bool empty)
    {
        var snapshot = SharePointListMetadataSnapshot.TryCreate(WebUrl, empty ? WebJson() : WebJson(ListRow()));

        snapshot.SupportsFields("Id,Title,BaseType,BaseTemplate,Hidden,IsCatalog,RootFolder/ServerRelativeUrl,RootFolder/UniqueId,DefaultViewUrl,DefaultDisplayFormUrl,ContentTypes/Id,ContentTypes/DisplayFormUrl")
            .Should().BeTrue();
        snapshot.SupportsFields(" RootFolder/UniqueId , Id ").Should().BeTrue();
    }

    [Theory]
    [InlineData("Fields/SchemaXml")]
    [InlineData("ContentTypes/Name")]
    [InlineData("Id,ContentTypes/StringId")]
    [InlineData("*")]
    [InlineData("RootFolder")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Id,,Title")]
    public void Snapshot_does_not_claim_columns_outside_its_declared_projection(string select)
    {
        var row = ListRow();
        row["DefaultDisplayFormUrl"] = "/sites/a/Documents/Forms/DispForm.aspx";
        row["ContentTypes"] = new[] { new { Id = "0x01", DisplayFormUrl = "" } };
        var snapshot = SharePointListMetadataSnapshot.TryCreate(WebUrl, WebJson(row));

        snapshot.Should().NotBeNull();
        snapshot.SupportsFields(select).Should().BeFalse();
    }

    [Fact]
    public void Rich_metadata_is_validated_but_only_discovery_fields_are_retained()
    {
        var row = ListRow();
        row["Fields"] = new[] { new { InternalName = "Title", SchemaXml = "large assessment metadata" } };
        row["ContentTypes"] = new[] { new { Id = new { StringValue = "0x01" }, DisplayFormUrl = "", Name = "Item" } };
        ((Dictionary<string, object>)row["RootFolder"])["Files"] = new[] { new { Name = "not-discovery-data" } };

        var snapshot = SharePointListMetadataSnapshot.TryCreate(WebUrl, WebJson(row), ReceivedAt);

        snapshot.Should().NotBeNull();
        var item = snapshot.Items.Should().ContainSingle().Which;
        item.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(new[]
        {
            "Id", "Title", "BaseType", "BaseTemplate", "Hidden", "IsCatalog", "RootFolder", "DefaultViewUrl",
            "DefaultDisplayFormUrl", "ContentTypes",
        });
        item.GetProperty("RootFolder").EnumerateObject().Select(property => property.Name).Should()
            .BeEquivalentTo(new[] { "ServerRelativeUrl", "UniqueId" });
        item.GetProperty("Id").GetGuid().Should().Be(ListId);
        item.GetProperty("DefaultViewUrl").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetRawText().Should().NotContain("SchemaXml").And.NotContain("Item");
        var contentType = item.GetProperty("ContentTypes").EnumerateArray().Should().ContainSingle().Which;
        contentType.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("Id", "DisplayFormUrl");
        contentType.GetProperty("Id").GetProperty("StringValue").GetString().Should().Be("0x01");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_shared_snapshot_avoids_all_list_collection_requests_and_records_model_authority(bool empty)
    {
        var snapshot = SharePointListMetadataSnapshot.TryCreate(WebUrl,
            empty ? WebJson() : WebJson(ListRow()), ReceivedAt);
        using var factory = new CountingFactory();
        using var provider = Provider(factory, snapshot);

        var result = await provider.EnumerateChildrenAsync(provider.RootScope);

        factory.Client.Requests.Should().BeEmpty();
        factory.Client.AllListReads.Should().Be(0);
        factory.Client.WelcomePageReads.Should().Be(1);
        result.ObservedChildren.Count.Should().Be(empty ? 1 : 2);
        result.ObservedChildren.Should().ContainSingle(scope => scope.Metadata["role"] == "web-root");
        var authority = ListAuthority(provider);
        authority.ActualMethod.Should().Be("MODEL");
        authority.ActualEndpoint.Should().StartWith("PnP.Core:Web.Lists").And.NotContain("_api");
        authority.AuthorityKind.Should().Be("PnPCoreSharedListMetadataAuthority");
        authority.ExpectedCount.Should().Be(empty ? 0 : 1);
        authority.ObservedCount.Should().Be(empty ? 0 : 1);
        authority.ExpectedCountState.Should().Be(AspxExpectedCountState.Known);
        authority.TerminalOutcome.Should().Be(empty ? DiscoveryTerminalOutcome.Empty : DiscoveryTerminalOutcome.Complete);
        authority.AsOfUtc.Should().Be(ReceivedAt);
        authority.ContinuationRemaining.Should().BeFalse();
        authority.PaginationOutstandingTokenCount.Should().Be(0);
        authority.EvidenceRefs.Should().Contain(value => value.StartsWith("shared-sdk-web-lists:"));
        provider.ReferenceCollector.ReadPaginationEvidence().Should().BeEmpty();
        provider.ReferenceCollector.ReadSurfaceEvidence().Should().NotContain(row => row.ActualMethod == "GET");
        if (empty) authority.AbsenceProofKind.Should().Be("AuthorityTerminalZero");
    }

    /// <summary>
    /// Reuses complete modeled form declarations across collection and ID shapes, retaining separate references without REST or item reads.
    /// </summary>
    [Theory]
    [InlineData("array", false)]
    [InlineData("array", true)]
    [InlineData("results", false)]
    [InlineData("results", true)]
    [InlineData("value", false)]
    public async Task Shared_content_type_forms_are_declarations_without_item_requests(
        string shape, bool flattenedId)
    {
        var row = ListRow(title: "Posts", path: "/sites/a/Lists/Posts", baseType: 0, template: 100);
        row["DefaultDisplayFormUrl"] = "/sites/a/Lists/Posts/DispForm.aspx";
        var forms = new[]
        {
            new Dictionary<string, object> { ["Id"] = flattenedId ? "0x0110" : new { StringValue = "0x0110" },
                ["DisplayFormUrl"] = "Lists/Posts/Post.aspx" },
            new Dictionary<string, object> { ["Id"] = flattenedId ? "0x0110007C4E" : new { StringValue = "0x0110007C4E" },
                ["DisplayFormUrl"] = "Lists/Posts/Special.aspx" },
        };
        row["ContentTypes"] = shape == "array" ? forms : new Dictionary<string, object>
        {
            [shape] = forms, ["@odata.count"] = 2,
        };
        var snapshot = SharePointListMetadataSnapshot.TryCreate(WebUrl, WebJson(row));
        snapshot.Should().NotBeNull();
        using var factory = new CountingFactory();
        using var provider = Provider(factory, snapshot);
        var list = (await provider.EnumerateChildrenAsync(provider.RootScope)).ObservedChildren
            .Single(scope => scope.Metadata["role"] == "list");
        (await provider.EnumerateChildrenAsync(list)).ObservedChildren
            .Should().NotContain(scope => scope.Locator.Contains("Items REST objects", StringComparison.Ordinal));
        var references = provider.ReferenceCollector.ReadReferenceEvidence();
        references.Should().ContainSingle(value =>
            value.SourceKind == AspxReferenceSourceKinds.ListDefaultDisplayForm &&
            value.RawLocator == "/sites/a/Lists/Posts/DispForm.aspx");
        references.Where(value => value.SourceKind == AspxReferenceSourceKinds.ContentTypeDisplayForm)
            .Select(value => (value.ContentTypeId, value.RawLocator))
            .Should().BeEquivalentTo(new[]
            {
                ("0x0110", "/sites/a/Lists/Posts/Post.aspx"),
                ("0x0110007C4E", "/sites/a/Lists/Posts/Special.aspx")
            });
        factory.Client.AllListReads.Should().Be(0);
        factory.Client.Requests.Should().BeEmpty();
        ListAuthority(provider).ActualMethod.Should().Be("MODEL");
    }

    [Theory]
    [InlineData("array")]
    [InlineData("results")]
    [InlineData("value")]
    public void Explicit_empty_content_types_are_complete_for_every_supported_wrapper(string shape)
    {
        var row = ListRow();
        row["ContentTypes"] = shape == "array" ? Array.Empty<object>() : new Dictionary<string, object>
        {
            [shape] = Array.Empty<object>(), ["__count"] = "0",
        };
        var snapshot = SharePointListMetadataSnapshot.TryCreate(WebUrl, WebJson(row));
        snapshot.Should().NotBeNull();
        snapshot.Items[0].GetProperty("ContentTypes").EnumerateArray().Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(UnusableSnapshots))]
    public async Task Invalid_or_partial_shared_metadata_falls_back_to_the_existing_paged_raw_authority(string reason, string json)
    {
        var snapshot = SharePointListMetadataSnapshot.TryCreate(WebUrl, json);
        snapshot.Should().BeNull(reason);
        using var factory = new CountingFactory { Client = { PageLists = true } };
        using var provider = Provider(factory, snapshot);

        var result = await provider.EnumerateChildrenAsync(provider.RootScope);

        factory.Client.AllListReads.Should().Be(2, reason);
        factory.Client.Requests.Should().HaveCount(2);
        factory.Client.Requests[1].Query.Should().Contain("skiptoken");
        result.ObservedChildren.Should().ContainSingle(scope => scope.Metadata["role"] == "list");
        var authority = ListAuthority(provider);
        authority.ActualMethod.Should().Be("GET");
        authority.AuthorityKind.Should().Be("SharePointRestCollectionAuthority");
        authority.TerminalOutcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        authority.ExpectedCountState.Should().Be(AspxExpectedCountState.Known);
        authority.ExpectedCount.Should().Be(1);
        provider.ReferenceCollector.ReadPaginationEvidence().Should().HaveCount(2);
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/b")]
    [InlineData("https://contoso.sharepoint.com/sites/a/subweb")]
    [InlineData("https://other.sharepoint.com/sites/a")]
    [InlineData("https://contoso.sharepoint.com:444/sites/a")]
    public async Task Foreign_web_snapshot_is_ignored_in_favor_of_raw_collection_authority(string foreignUrl)
    {
        var snapshot = SharePointListMetadataSnapshot.TryCreate(new Uri(foreignUrl), WebJson());
        using var factory = new CountingFactory();
        using var provider = Provider(factory, snapshot);

        var result = await provider.EnumerateChildrenAsync(provider.RootScope);

        factory.Client.AllListReads.Should().Be(1);
        result.ObservedChildren.Should().ContainSingle(scope => scope.Metadata["role"] == "list");
        ListAuthority(provider).ActualMethod.Should().Be("GET");
    }

    [Fact]
    public async Task Normalized_same_web_snapshot_is_reused_without_a_collection_request()
    {
        var snapshot = SharePointListMetadataSnapshot.TryCreate(
            new Uri("https://CONTOSO.sharepoint.com:443/sites/a/"), WebJson());
        using var factory = new CountingFactory();
        using var provider = Provider(factory, snapshot);

        await provider.EnumerateChildrenAsync(provider.RootScope);

        factory.Client.AllListReads.Should().Be(0);
        ListAuthority(provider).ActualMethod.Should().Be("MODEL");
    }

    /// <summary>
    /// Requires shared and raw list authority to expose the same hidden, catalog, and ordinary-list folder and reference surfaces,
    /// while reusing one modeled web-root read and retaining its physical file.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_and_raw_lists_preserve_root_hidden_catalog_generic_and_all_traversal_surfaces(bool share)
    {
        var rows = new[]
        {
            ListRow(title: "Hidden library", path: "/sites/a/Hidden", hidden: true),
            ListRow(Guid.Parse("44444444-4444-4444-4444-444444444444"), "Catalog", "/sites/a/_catalogs/masterpage",
                template: 116, hidden: true, catalog: true),
            ListRow(Guid.Parse("55555555-5555-5555-5555-555555555555"), "Tasks", "/sites/a/Lists/Tasks", baseType: 0, template: 100),
        };
        var snapshot = share ? SharePointListMetadataSnapshot.TryCreate(WebUrl, WebJson(rows)) : null;
        using var factory = new CountingFactory();
        factory.Client.RawLists = rows.Select(row => JsonSerializer.SerializeToElement(row)).ToArray();
        using var provider = Provider(factory, snapshot);

        var (scopes, records) = await TraverseAsync(provider);

        factory.Client.AllListReads.Should().Be(share ? 0 : 1);
        factory.Client.Requests.Count(uri => uri.AbsolutePath.EndsWith("/Forms")).Should().Be(3);
        factory.Client.Requests.Count(uri => uri.AbsolutePath.EndsWith("/Views")).Should().Be(3);
        factory.Client.Requests.Count(uri => uri.AbsolutePath.EndsWith("/Files")).Should().Be(6);
        factory.Client.Requests.Count(uri => uri.AbsolutePath.EndsWith("/Folders")).Should().Be(6);
        factory.Client.FolderReads.Should().Be(1, "root files and folders share the same modeled root result");
        var lists = scopes.Where(scope => scope.Metadata["role"] == "list").ToArray();
        lists.Should().HaveCount(3);
        lists.Single(scope => scope.Metadata["title"] == "Hidden library").Metadata["hidden"].Should().Be("True");
        lists.Single(scope => scope.Metadata["title"] == "Catalog").Metadata["isCatalog"].Should().Be("True");
        lists.Single(scope => scope.Metadata["title"] == "Tasks").Metadata["baseType"].Should().Be("0");
        scopes.Count(scope => scope.Metadata["role"] == "folder").Should().Be(3);
        scopes.Count(scope => scope.Metadata["role"] == "physical-forms").Should().Be(3);
        scopes.Count(scope => scope.Metadata["role"] == "forms").Should().Be(3);
        scopes.Count(scope => scope.Metadata["role"] == "views").Should().Be(3);
        records.Should().ContainSingle(record => record.PhysicalLocator == "/sites/a/default.aspx");
    }

    /// <summary>
    /// Discovers real ASPX files under an ordinary list root and its nested folder with the observed list scope, without item enumeration.
    /// </summary>
    [Fact]
    public async Task Ordinary_list_root_and_nested_folder_discover_physical_aspx_without_item_enumeration()
    {
        var tasks = ListRow(title: "Tasks", path: "/sites/a/Lists/Tasks", baseType: 0, template: 100);
        using var factory = new CountingFactory();
        factory.Client.RawLists = new[] { JsonSerializer.SerializeToElement(tasks) };
        factory.Client.IncludeTaskFiles = true;
        using var provider = Provider(factory, null);

        var (scopes, records) = await TraverseAsync(provider);

        scopes.Should().Contain(scope => scope.Metadata["role"] == "folder" &&
            scope.SourceKind == DiscoverySourceKind.RawListFolderFiles);
        records.Should().Contain(record => record.PhysicalLocator == "/sites/a/Lists/Tasks/DispForm.aspx" &&
            record.ListId == ListId);
        records.Should().Contain(record => record.PhysicalLocator == "/sites/a/Lists/Tasks/Nested/Custom.aspx" &&
            record.ListId == ListId);
        factory.Client.Requests.Should().NotContain(uri => uri.AbsolutePath.EndsWith("/items", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Home_page_only_keeps_targeted_acquisition_and_never_consumes_list_snapshot(bool share)
    {
        var snapshot = share ? SharePointListMetadataSnapshot.TryCreate(WebUrl, WebJson(ListRow())) : null;
        using var factory = new CountingFactory { Client = { WelcomePage = "SitePages/Home.aspx" } };
        using var provider = Provider(factory, snapshot, AspxDiscoveryIntent.HomePageOnly);

        var (scopes, records) = await TraverseAsync(provider);

        factory.Client.WelcomePageReads.Should().Be(1);
        factory.Client.FileResolutions.Should().Be(1);
        factory.Client.AllListReads.Should().Be(0);
        factory.Client.Requests.Should().BeEmpty();
        factory.Client.FolderReads.Should().Be(0);
        scopes.Should().ContainSingle(scope => scope.Metadata["role"] == "welcome-page");
        scopes.Should().NotContain(scope => scope.Metadata["role"] == "list" || scope.Metadata["role"] == "web-root");
        records.Should().ContainSingle(record => record.HomePage == true && record.ListId == ListId &&
            record.PhysicalLocator == "/sites/a/SitePages/Home.aspx");
        provider.ReferenceCollector.ReadSurfaceEvidence().Should().NotContain(row => row.RequiredAdapter == "AllListsAuthority");
    }

    /// <summary>A rejected shared snapshot and failed enriched/minimal list authorities remain denied, never a successful empty census.</summary>
    [Fact]
    public async Task Rejected_snapshot_does_not_turn_raw_authority_failure_into_a_known_empty_collection()
    {
        var snapshot = SharePointListMetadataSnapshot.TryCreate(WebUrl, "{\"Lists\":[],\"Lists@odata.nextLink\":\"next\"}");
        using var factory = new CountingFactory { Client = { DenyLists = true } };
        using var provider = Provider(factory, snapshot);

        await provider.EnumerateChildrenAsync(provider.RootScope);

        factory.Client.AllListReads.Should().Be(2, "failed enriched list metadata is followed by an independent physical-root projection");
        var authority = ListAuthority(provider);
        authority.ActualMethod.Should().Be("GET");
        authority.TerminalOutcome.Should().Be(DiscoveryTerminalOutcome.Denied);
        authority.ExpectedCountState.Should().Be(AspxExpectedCountState.Unknown);
        authority.ExpectedCount.Should().BeNull();
        authority.AbsenceProofKind.Should().BeNull();
        provider.ReferenceCollector.ReadPaginationEvidence().Should().HaveCount(2);
    }

    public static IEnumerable<object[]> UnusableSnapshots()
    {
        var duplicate = ListRow(title: "Duplicate ID representation");
        duplicate["Id"] = ListId.ToString("B").ToUpperInvariant();
        yield return new object[] { "duplicate list ID, regardless of GUID representation", WebJson(ListRow(), duplicate) };

        foreach (var json in new[] { "not-json", "null", "[]", "{}", "{\"Lists\":null}", "{\"Lists\":{}}",
            "{\"value\":[]}", "{\"Lists\":[],\"Lists@odata.count\":1}",
            "{\"d\":{\"Lists\":{\"results\":[],\"__count\":\"1\"}}}",
            "{\"Lists\":[],\"Lists\":[]}", "{\"Lists\":{\"results\":[],\"value\":[]}}",
            "{\"error\":{},\"d\":{\"Lists\":[]}}", "{\"Lists\":[],\"value\":{\"Lists\":[]}}",
            "{\"d\":{\"Lists\":[]},\"value\":{\"Lists\":[]}}" })
            yield return new object[] { "missing, malformed, ambiguous or partial collection", json };

        foreach (var field in new[] { "Id", "Title", "BaseType", "BaseTemplate", "Hidden", "IsCatalog", "RootFolder", "DefaultViewUrl", "DefaultDisplayFormUrl", "ContentTypes" })
        {
            var missing = ListRow();
            missing.Remove(field);
            yield return new object[] { "missing " + field, WebJson(missing) };
        }
        foreach (var (field, value) in new (string, object)[]
        {
            ("Id", "not-a-guid"), ("Id", Guid.Empty), ("Title", null), ("BaseType", "1"), ("BaseTemplate", 101.5),
            ("Hidden", "false"), ("IsCatalog", 0), ("RootFolder", null), ("DefaultViewUrl", false),
            ("DefaultDisplayFormUrl", false), ("ContentTypes", null), ("ContentTypes", new { }),
        })
        {
            var invalid = ListRow();
            invalid[field] = value;
            yield return new object[] { "invalid " + field, WebJson(invalid) };
        }
        foreach (var (field, value) in new (string, object)[]
        {
            ("UniqueId", "bad-guid"), ("UniqueId", Guid.Empty), ("ServerRelativeUrl", null), ("ServerRelativeUrl", "relative/path"),
        })
        {
            var invalid = ListRow();
            ((Dictionary<string, object>)invalid["RootFolder"])[field] = value;
            yield return new object[] { "invalid RootFolder." + field, WebJson(invalid) };
        }
        foreach (var contentTypes in new object[]
        {
            new[] { new { Id = "0x01" } },
            new[] { new { DisplayFormUrl = "Lists/Posts/Post.aspx" } },
            new[] { new { Id = new { StringValue = (string)null }, DisplayFormUrl = "" } },
            new[] { new { Id = 123, DisplayFormUrl = "" } },
            new[] { new { Id = "", DisplayFormUrl = "" } },
            new[] { new { Id = "0x01", DisplayFormUrl = false } },
            new[] { new { Id = "0x01", DisplayFormUrl = "" }, new { Id = "0X01", DisplayFormUrl = "" } },
            new { results = Array.Empty<object>(), value = Array.Empty<object>() },
            new Dictionary<string, object> { ["results"] = Array.Empty<object>(), ["__count"] = "1" },
            new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.count"] = 1 },
        })
        {
            var partial = ListRow();
            partial["ContentTypes"] = contentTypes;
            yield return new object[] { "invalid or partial content-type form metadata", WebJson(partial) };
        }
        var partialCount = ListRow();
        partialCount["ContentTypes@odata.count"] = 1;
        yield return new object[] { "partial content types count on parent list", WebJson(partialCount) };
        foreach (var marker in new[] { "__next", "odata.nextLink", "@odata.nextLink", "Lists@odata.nextLink", "NextHref", "continuationToken" })
        {
            yield return new object[] { "Web continuation " + marker,
                JsonSerializer.Serialize(new Dictionary<string, object> { ["Lists"] = new[] { ListRow() }, [marker] = "next" }) };
            yield return new object[] { "Lists wrapper continuation " + marker,
                JsonSerializer.Serialize(new { Lists = new Dictionary<string, object> { ["results"] = new[] { ListRow() }, [marker] = "next" } }) };
            var nested = ListRow();
            nested["Fields"] = new Dictionary<string, object> { ["results"] = Array.Empty<object>(), [marker] = "next" };
            yield return new object[] { "nested child continuation " + marker, WebJson(nested) };
            var continuedForms = ListRow();
            continuedForms["ContentTypes"] = new Dictionary<string, object> { ["results"] = Array.Empty<object>(), [marker] = "next" };
            yield return new object[] { "content types continuation " + marker, WebJson(continuedForms) };
        }
        var parentContinuation = ListRow();
        parentContinuation["ContentTypes@odata.nextLink"] = "next";
        yield return new object[] { "content types parent continuation", WebJson(parentContinuation) };
    }

    private static Dictionary<string, object> ListRow(Guid? id = null, string title = "Documents",
        string path = "/sites/a/Documents", int baseType = 1, int template = 101, bool hidden = false, bool catalog = false) => new()
    {
        ["Id"] = id ?? ListId, ["Title"] = title, ["BaseType"] = baseType, ["BaseTemplate"] = template,
        ["Hidden"] = hidden, ["IsCatalog"] = catalog,
        ["RootFolder"] = new Dictionary<string, object> { ["ServerRelativeUrl"] = path, ["UniqueId"] = FolderId },
        ["DefaultViewUrl"] = null, ["DefaultDisplayFormUrl"] = null, ["ContentTypes"] = Array.Empty<object>(),
    };

    private static string WebJson(params Dictionary<string, object>[] rows) => JsonSerializer.Serialize(new { Lists = rows });

    private static SharePointLiveAspxDiscoveryProvider Provider(CountingFactory factory,
        SharePointListMetadataSnapshot snapshot, AspxDiscoveryIntent intent = AspxDiscoveryIntent.FullInventory) => new(
        new SharePointLiveAspxDiscoveryOptions("test", "test-web", "shared-test-v1", new string('a', 64), Intent: intent),
        factory, new AspxWebAcquisitionContext(Guid.NewGuid(), WebUrl, Guid.NewGuid(), WebUrl, "/sites/a", "STS#3"), snapshot);

    private static AspxSurfaceDenominatorRow ListAuthority(SharePointLiveAspxDiscoveryProvider provider) =>
        provider.ReferenceCollector.ReadSurfaceEvidence().Single(row => row.RequiredAdapter == "AllListsAuthority");

    private static async Task<(List<DiscoveryScopeRegistration> Scopes, List<RawDiscoveryRecord> Records)> TraverseAsync(
        SharePointLiveAspxDiscoveryProvider provider)
    {
        var scopes = new List<DiscoveryScopeRegistration>();
        var records = new List<RawDiscoveryRecord>();
        var queue = new Queue<DiscoveryScopeRegistration>();
        queue.Enqueue(provider.RootScope);
        while (queue.TryDequeue(out var scope))
        {
            scopes.Add(scope);
            var source = provider.CreateRawSource(scope);
            if (source != null)
                await foreach (var batch in source.ReadBatchesAsync()) records.AddRange(batch.Records);
            foreach (var child in (await provider.EnumerateChildrenAsync(scope)).ObservedChildren) queue.Enqueue(child);
        }
        return (scopes, records);
    }

    private sealed class CountingFactory : ISharePointAspxRestClientFactory
    {
        internal CountingClient Client { get; } = new();
        public Task<ISharePointAspxRestClient> GetAsync(Uri webUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult<ISharePointAspxRestClient>(Client);
        public void Dispose() { }
    }

    private sealed class CountingClient : ISharePointAspxRestClient
    {
        public Uri WebUrl => SharedListMetadataTests.WebUrl;
        internal List<Uri> Requests { get; } = new();
        internal IReadOnlyList<JsonElement> RawLists { get; set; } = new[] { JsonSerializer.SerializeToElement(ListRow()) };
        internal IReadOnlyList<JsonElement> Items { get; set; } = Array.Empty<JsonElement>();
        internal bool PageLists { get; set; }
        internal bool DenyLists { get; set; }
        internal bool IncludeTaskFiles { get; set; }
        internal string WelcomePage { get; set; }
        internal int AllListReads { get; private set; }
        internal int WelcomePageReads { get; private set; }
        internal int FileResolutions { get; private set; }
        internal int FolderReads { get; private set; }

        /// <summary>
        /// Records offline REST requests and supplies configurable list authority, denial, continuations, item rows, and task-folder facts.
        /// </summary>
        public Task<SharePointRestPage> GetPageAsync(Uri requestUri, CancellationToken cancellationToken = default)
        {
            Requests.Add(requestUri);
            var items = (IReadOnlyList<JsonElement>)Array.Empty<JsonElement>();
            string next = null;
            var outcome = DiscoveryTerminalOutcome.Complete;
            if (requestUri.AbsolutePath.EndsWith("/_api/web/lists", StringComparison.OrdinalIgnoreCase))
            {
                AllListReads++;
                if (DenyLists) outcome = DiscoveryTerminalOutcome.Denied;
                else if (AllListReads == 1)
                {
                    items = RawLists;
                    if (PageLists) next = WebUrl.AbsoluteUri + "/_api/web/lists?$skiptoken=second";
                }
            }
            else if (requestUri.AbsolutePath.EndsWith("/items", StringComparison.OrdinalIgnoreCase)) items = Items;
            else if (IncludeTaskFiles)
            {
                var folder = WebUtility.UrlDecode(requestUri.Query);
                if (folder.Contains("/Lists/Tasks/Nested", StringComparison.OrdinalIgnoreCase) &&
                    requestUri.AbsolutePath.EndsWith("/Files", StringComparison.OrdinalIgnoreCase))
                    items = new[] { JsonSerializer.SerializeToElement(new
                    {
                        UniqueId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                        Name = "Custom.aspx", ServerRelativeUrl = "/sites/a/Lists/Tasks/Nested/Custom.aspx",
                        CustomizedPageStatus = "Uncustomized",
                    }) };
                else if (folder.Contains("/Lists/Tasks", StringComparison.OrdinalIgnoreCase) &&
                         requestUri.AbsolutePath.EndsWith("/Files", StringComparison.OrdinalIgnoreCase))
                    items = new[] { JsonSerializer.SerializeToElement(new
                    {
                        UniqueId = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                        Name = "DispForm.aspx", ServerRelativeUrl = "/sites/a/Lists/Tasks/DispForm.aspx",
                        CustomizedPageStatus = "Uncustomized",
                    }) };
                else if (folder.Contains("/Lists/Tasks", StringComparison.OrdinalIgnoreCase) &&
                         !folder.Contains("/Nested", StringComparison.OrdinalIgnoreCase) &&
                         requestUri.AbsolutePath.EndsWith("/Folders", StringComparison.OrdinalIgnoreCase))
                    items = new[] { JsonSerializer.SerializeToElement(new
                    {
                        UniqueId = Guid.Parse("88888888-8888-8888-8888-888888888888"),
                        Name = "Nested", ServerRelativeUrl = "/sites/a/Lists/Tasks/Nested",
                    }) };
            }
            return Task.FromResult(new SharePointRestPage(requestUri,
                DenyLists ? HttpStatusCode.Forbidden : HttpStatusCode.OK, items, next,
                DiscoveryHash.Of(requestUri.AbsoluteUri), "odata-nometadata", outcome,
                outcome == DiscoveryTerminalOutcome.Denied ? "access_denied" : null, ReceivedAtUtc: ReceivedAt));
        }

        public Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        {
            FileResolutions++;
            return Task.FromResult(new SharePointResolvedFile(DiscoveryTerminalOutcome.Complete, FileId.ToString("D"),
                "Home.aspx", serverRelativeUrl, "Customized", "fake:resolve", ListId: ListId, ListItemId: 7));
        }

        public Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken cancellationToken = default)
        {
            WelcomePageReads++;
            return Task.FromResult(new SharePointModeledValue(WelcomePage == null ? DiscoveryTerminalOutcome.Empty : DiscoveryTerminalOutcome.Complete,
                WelcomePage, "fake", "welcome", null, "fake:welcome", ReceivedAt));
        }

        public Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        {
            FolderReads++;
            return Task.FromResult(new SharePointModeledFolderResult(DiscoveryTerminalOutcome.Complete, FolderId.ToString("D"),
                serverRelativeUrl, Array.Empty<SharePointModeledFolder>(),
                new[] { new SharePointModeledFile(FileId.ToString("D"), "default.aspx", "/sites/a/default.aspx", "Uncustomized") },
                "fake", "folder", null, "fake:folder", ReceivedAt));
        }

        public void Dispose() { }
    }
}
