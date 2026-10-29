using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Discovery.Aspx;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.Aspx;

public sealed class ClassicAspxWebAcquisitionTests
{
    private static readonly Guid Scan = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Site = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Web = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid List = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid Renderer = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Uri SiteUrl = new("https://tenant.invalid/sites/fixture");
    private static readonly Uri WebUrl = new("https://tenant.invalid/sites/fixture/child");
    private const string Form = "/sites/fixture/child/Lists/hidden/DispForm.aspx";

    /// <summary>
    /// Builds a pages-enabled acquisition context with configurable publication and traversal bounds and optional frozen catalog.
    /// </summary>
    private static ClassicAspxWebAcquisitionContext Context(int batchSize = 7, int maxPages = 8, PlatformAspxCatalog catalog = null) =>
        new(Scan, Site, Web, SiteUrl, WebUrl, new ClassicOptions { Pages = true }, catalog,
            BatchSize: batchSize, MaxCollectionPages: maxPages);
    /// <summary>
    /// Creates an OData JSON page through the real response parser, retaining optional continuation evidence.
    /// </summary>
    private static SharePointRestPage Page(Uri uri, object[] rows, string next = null) =>
        SharePointRestResponseParser.Parse(uri, HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        { ["value"] = rows, ["@odata.nextLink"] = next }), "application/json");
    /// <summary>
    /// Yields caller-provided physical discoveries in order without adding synthetic page or item observations.
    /// </summary>
    private static async IAsyncEnumerable<ClassicPageDiscovery> Observations(params ClassicPageDiscovery[] rows)
    { foreach (var row in rows) yield return row; await Task.CompletedTask; }
    /// <summary>
    /// Returns successful metadata with the supplied real file identity and path for locator-resolution scenarios.
    /// </summary>
    private static SharePointResolvedFile File(Guid id, string path) => new(DiscoveryTerminalOutcome.Complete, id.ToString("D"),
        path.Split('/')[^1], path, null, "FakeAuthenticatedAPI:FileMetadata");
    /// <summary>
    /// Supplies authenticated actual-source evidence while allowing tests to mark the same bytes explicitly denied.
    /// </summary>
    private static SharePointFileContent Bytes(AspxHandlerReadState read = AspxHandlerReadState.Available) =>
        new(read == AspxHandlerReadState.Denied ? DiscoveryTerminalOutcome.Denied : DiscoveryTerminalOutcome.Complete,
            Encoding.UTF8.GetBytes("<%@ Page Inherits='Example.ActualHandler' %>"), true,
            AspxHandlerSourceKind.ActualFileBytes, read, "FakeAuthenticatedAPI:$value", SameAuthorityConfirmed: true);

    private sealed class Client : ISharePointAspxRestClient
    {
        internal Uri ActualWebUrl = ClassicAspxWebAcquisitionTests.WebUrl;
        public Uri WebUrl => ActualWebUrl;
        internal Func<Uri, SharePointRestPage> Pages;
        internal Func<string, SharePointResolvedFile> Resolver;
        internal Func<string, SharePointFileContent> Content = _ => Bytes();
        internal readonly List<Uri> Requests = new();
        internal readonly List<string> Resolutions = new();
        internal int ContentReads;
        internal readonly List<string> ContentPaths = new();
        /// <summary>
        /// Records collection requests, honors pre-cancellation, and delegates responses to the configured fake authority.
        /// </summary>
        public Task<SharePointRestPage> GetPageAsync(Uri uri, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Requests.Add(uri); return Task.FromResult(Pages?.Invoke(uri) ?? Page(uri, Array.Empty<object>())); }
        /// <summary>
        /// Records locator resolutions and returns configured metadata or the stable synthetic renderer identity.
        /// </summary>
        public Task<SharePointResolvedFile> ResolveFileAsync(string path, CancellationToken ct = default)
        { Resolutions.Add(path); return Task.FromResult(Resolver?.Invoke(path) ?? File(Renderer, path)); }
        /// <summary>
        /// Counts and records source reads before returning the scenario-specific complete or failed content evidence.
        /// </summary>
        public Task<SharePointFileContent> ReadFileContentAsync(string path, int maxBytes, TimeSpan timeout, CancellationToken ct = default)
        { ContentReads++; ContentPaths.Add(path); return Task.FromResult(Content(path)); }
        /// <summary>
        /// Fails unexpected welcome-page API access because acquisition scenarios provide their own physical observations.
        /// </summary>
        public Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken ct = default) => throw new NotSupportedException();
        /// <summary>
        /// Fails unexpected folder API access so tests cannot silently depend on an unmodeled discovery surface.
        /// </summary>
        public Task<SharePointModeledFolderResult> ReadFolderAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
        /// <summary>
        /// Releases no external resources because this fake client only records in-memory calls.
        /// </summary>
        public void Dispose() { }
    }
    /// <summary>
    /// Returns the supplied root client and records factory requests and disposal without taking ownership of the fake client.
    /// </summary>
    private sealed class Factory(Client root) : ISharePointAspxRestClientFactory
    {
        internal readonly List<Uri> Requests = new();
        internal bool Disposed;
        /// <summary>
        /// Records root-client requests, checks cancellation, and returns the configured client for scope-validation tests.
        /// </summary>
        public Task<ISharePointAspxRestClient> GetAsync(Uri web, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Requests.Add(web); return Task.FromResult<ISharePointAspxRestClient>(root); }
        /// <summary>
        /// Records factory disposal so tests can detect accidental disposal of a caller-owned root-client factory.
        /// </summary>
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Creates a hidden non-document list with configurable real items, content types, and list form renderer.
    /// </summary>
    private static Client Items(object[] items, object[] types = null, string form = Form) => new()
    {
        Pages = uri => uri.AbsolutePath.EndsWith("/lists", StringComparison.OrdinalIgnoreCase)
            ? Page(uri, new object[] { new { Id = List, Hidden = true, BaseType = 0, BaseTemplate = 100, DefaultDisplayFormUrl = form } })
            : uri.AbsolutePath.EndsWith("/ContentTypes", StringComparison.OrdinalIgnoreCase)
                ? Page(uri, types ?? new object[] { new { Id = new { StringValue = "0x01" }, DisplayFormUrl = "" } })
                : Page(uri, items)
    };
    private sealed class Collected
    {
        internal readonly List<ClassicAspxInventory> Inventory = new();
        internal readonly List<ClassicAspxItemBinding> Bindings = new();
        internal readonly List<ClassicPageDiscovery> Evidence = new();
        internal int LargestBatch;
        /// <summary>
        /// Collects all three publication streams and records the largest combined batch without altering emitted facts.
        /// </summary>
        internal Task Publish(ClassicAspxAcquisitionBatch batch, CancellationToken ct)
        {
            LargestBatch = Math.Max(LargestBatch, batch.Inventory.Count + batch.Bindings.Count + batch.Evidence.Count);
            Inventory.AddRange(batch.Inventory); Bindings.AddRange(batch.Bindings); Evidence.AddRange(batch.Evidence);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Includes real items from hidden non-document lists, bounds publications, and shares one renderer source read across item bindings.
    /// </summary>
    [Fact]
    public async Task All_real_items_including_hidden_non_document_lists_are_bounded_and_not_fixture_counted()
    {
        var client = Items(Enumerable.Range(1, 73).Select(i => (object)new { Id = i, Title = "real " + i, ContentTypeId = "0x01" }).ToArray());
        var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(), client).AcquireAsync(Observations(), found.Publish);
        Assert.Equal(73, found.Bindings.Count);
        Assert.Equal(73, found.Bindings.Select(b => b.ItemId).Distinct().Count());
        Assert.All(found.Bindings, b => { Assert.Equal(Web, b.WebId); Assert.Null(b.ItemGuid); Assert.NotEqual(Guid.Empty, b.ListId); });
        Assert.Single(found.Inventory);
        Assert.Equal(1, client.ContentReads);
        Assert.InRange(found.LargestBatch, 1, 7);
        Assert.All(client.Requests, r => Assert.DoesNotContain("$filter", r.Query));
        Assert.Contains(found.Evidence, e => e.RowType == "Scope" && e.DiscoveryStatus == "Complete");
    }

    /// <summary>
    /// Falls back to the list form when the most-specific matching content type has no override, ignoring a less-specific override.
    /// </summary>
    [Fact]
    public async Task Most_specific_empty_ct_override_uses_list_default_not_ancestor_override()
    {
        var client = Items(new object[] { new { Id = 9, ContentTypeId = "0x0100AA01" } }, new object[]
        { new { Id = new { StringValue = "0x01" }, DisplayFormUrl = "/sites/fixture/incorrect.aspx" },
          new { Id = new { StringValue = "0x0100AA" }, DisplayFormUrl = "" } });
        var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(), client).AcquireAsync(Observations(), found.Publish);
        Assert.Single(found.Bindings);
        Assert.Equal(ClassicAspxBindingKinds.ListFormRenderer, found.Bindings[0].BindingKinds);
        Assert.Equal(Form, client.Resolutions.Single());
    }

    /// <summary>
    /// Keeps file-self, metadata-renderer, and parent-layout bindings distinct while preserving physical identities and item-web scope.
    /// </summary>
    [Fact]
    public async Task Actual_aspx_backing_metadata_renderer_and_shared_parent_layout_keep_real_identity_and_item_web()
    {
        string page = "/sites/fixture/child/Pages/one.aspx", layout = "/sites/fixture/_catalogs/masterpage/shared.aspx";
        Guid pageId = Guid.Parse("50000000-0000-0000-0000-000000000002"), layoutId = Guid.Parse("50000000-0000-0000-0000-000000000003");
        var client = Items(new object[] { new { Id = 21, GUID = Guid.Empty, Title = "page", ContentTypeId = "0x01",
            File = new { UniqueId = pageId, Name = "one.aspx", ServerRelativeUrl = page },
            PublishingPageLayout = new { Url = "https://tenant.invalid" + layout } },
            new { Id = 22, Title = "metadata", ContentTypeId = "0x01", File = new { UniqueId = Guid.NewGuid(), ServerRelativeUrl = "/sites/fixture/child/docs/not-aspx.txt" } } });
        client.Resolver = path => File(Renderer, path);
        var root = new Client { ActualWebUrl = SiteUrl, Resolver = path => File(layoutId, path) };
        var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(), client, new Factory(root)).AcquireAsync(Observations(), found.Publish);
        Assert.Contains(found.Inventory, x => x.AspxKey == ClassicAspxInventory.CreateTenantFileKey(Site, pageId));
        Assert.DoesNotContain(found.Inventory, x => x.Url.EndsWith(".txt"));
        var parent = Assert.Single(found.Inventory.Where(x => x.FileUniqueId == layoutId));
        Assert.Null(parent.WebId); // Never label the parent layout as owned by the child worker web.
        Assert.Equal(Site, parent.SiteCollectionId);
        Assert.Contains(found.Bindings, b => b.ItemId == 21 && b.BindingKinds == ClassicAspxBindingKinds.FileSelf);
        Assert.Contains(found.Bindings, b => b.ItemId == 21 && b.BindingKinds == ClassicAspxBindingKinds.PublishingPageLayout && b.WebId == Web);
        Assert.Contains(found.Bindings, b => b.ItemId == 22 && b.BindingKinds.HasFlag(ClassicAspxBindingKinds.DocumentMetadataRenderer));
    }

    /// <summary>
    /// Retains a legacy list-item page pointer as tenant inventory without inventing file-self bindings or catalog identity from SetupPath.
    /// </summary>
    [Fact]
    public async Task Legacy_listitem_page_pointer_is_not_its_file_self_and_setup_path_is_not_catalog_identity()
    {
        var client = new Client();
        var found = new Collected();
        var observation = new ClassicPageDiscovery { ScanId = Scan, RowType = "Page", ScopeType = "ListItem", SiteCollectionId = Site,
            WebId = Web, ListId = List, ListItemId = 7, FileUniqueId = Renderer, Url = Form, SetupPath = "_layouts/15/template.aspx" };
        await new ClassicAspxWebAcquisition(Context(), client).AcquireAsync(Observations(observation), found.Publish);
        Assert.Empty(found.Bindings);
        var physical = Assert.Single(found.Inventory);
        Assert.Equal(ClassicAspxIdentityKinds.TenantFile, physical.IdentityKind);
        Assert.Null(physical.WebId);
    }

    /// <summary>
    /// Preserves observed inventory and item bindings after content denial without supplying a handler or framework-default claim.
    /// </summary>
    [Fact]
    public async Task Denied_content_keeps_observed_inventory_but_never_handler_default()
    {
        var client = Items(new object[] { new { Id = 4, ContentTypeId = "0x01" } });
        client.Content = _ => Bytes(AspxHandlerReadState.Denied);
        var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(), client).AcquireAsync(Observations(), found.Publish);
        var row = Assert.Single(found.Inventory);
        Assert.Equal("Denied", row.HandlerReadState); Assert.Null(row.HandlerEffectiveType); Assert.Null(row.HandlerDefaultProvenance);
        Assert.Single(found.Bindings);
    }

    /// <summary>
    /// Retains a denied content-type scope as a coverage gap instead of inventing a list-default renderer binding.
    /// </summary>
    [Fact]
    public async Task Denied_ct_scope_does_not_invent_a_list_default_renderer()
    {
        var client = Items(new object[] { new { Id = 4, ContentTypeId = "0x01" } });
        var original = client.Pages;
        client.Pages = u => u.AbsolutePath.EndsWith("/ContentTypes", StringComparison.OrdinalIgnoreCase)
            ? SharePointRestResponseParser.Parse(u, HttpStatusCode.Forbidden, Encoding.UTF8.GetBytes("denied")) : original(u);
        var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(), client).AcquireAsync(Observations(), found.Publish);
        Assert.Empty(found.Bindings);
        Assert.Contains(found.Evidence, e => e.RowType == "Scope" && e.DiscoveryStatus == "Denied");
        Assert.Contains(found.Evidence, e => e.ErrorCodes == "content_type_scope_incomplete_no_renderer_fallback");
    }

    /// <summary>
    /// Stops unsafe or bounded-out continuations and records incomplete authority rather than reporting an empty collection.
    /// </summary>
    [Theory]
    [InlineData("foreign")]
    [InlineData("loop")]
    [InlineData("cap")]
    public async Task Incomplete_authority_never_claims_empty_and_never_follows_unsafe_cursor(string fault)
    {
        var client = new Client { Pages = u => Page(u, Array.Empty<object>(), fault == "foreign" ? "https://foreign.invalid/_api/web/lists" : fault == "loop" ? u.AbsoluteUri : u.AbsoluteUri + "&$skiptoken=second") };
        var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(maxPages: 1), client).AcquireAsync(Observations(), found.Publish);
        Assert.Single(client.Requests);
        Assert.DoesNotContain(found.Evidence, e => e.RowType == "Scope" && e.DiscoveryStatus == "Empty");
        Assert.Contains(found.Evidence, e => e.RowType == "Scope" && e.DiscoveryStatus is "Unknown" or "Truncated");
    }

    /// <summary>
    /// Consumes a valid continuation, rejects nonpositive item IDs, and keeps raw cursor material out of stored pagination evidence.
    /// </summary>
    [Fact]
    public async Task Actual_continuation_is_consumed_and_invalid_item_ids_do_not_create_bindings()
    {
        var client = Items(Array.Empty<object>()); var original = client.Pages;
        client.Pages = u => u.AbsolutePath.EndsWith("/items", StringComparison.OrdinalIgnoreCase)
            ? Page(u, u.Query.Contains("skiptoken") ? new object[] { new { Id = 8, ContentTypeId = "0x01" } } : new object[] { new { Id = 0, ContentTypeId = "0x01" } },
                u.Query.Contains("skiptoken") ? null : u.AbsoluteUri + "&$skiptoken=next") : original(u);
        var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(), client).AcquireAsync(Observations(), found.Publish);
        Assert.Equal(8, Assert.Single(found.Bindings).ItemId);
        Assert.Equal(2, client.Requests.Count(r => r.AbsolutePath.EndsWith("/items")));
        Assert.Contains(found.Evidence, e => e.ErrorCodes == "item_positive_id_missing");
        Assert.All(found.Evidence.Where(e => e.RowType == "Pagination"), e => Assert.False(e.EvidenceJson.Contains("skiptoken=next", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Propagates publication failures to the caller instead of misclassifying persistence errors as network coverage gaps.
    /// </summary>
    [Fact]
    public async Task Storage_callback_failure_is_not_swallowed_as_a_network_gap()
    {
        var client = new Client();
        await Assert.ThrowsAsync<IOException>(() => new ClassicAspxWebAcquisition(Context(batchSize: 1), client)
            .AcquireAsync(Observations(), (_, _) => throw new IOException("synthetic storage failure")));
        Assert.Single(client.Requests);
    }

    /// <summary>
    /// Builds one explicit platform path or two ambiguous candidates for testing catalog-only renderer resolution.
    /// </summary>
    private static PlatformAspxCatalog Catalog(bool ambiguous = false)
    {
        object Entry(string id) => new { id, deploymentIdentity = id, culture = "en-US", path = "/_layouts/15/listform.aspx" };
        return PlatformAspxCatalog.Parse(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "platform-aspx-catalog/v1", catalogVersion = "1", source = "FakeFrozenCatalog", entries = ambiguous ? new[] { Entry("a"), Entry("b") } : new[] { Entry("a") } }));
    }

    /// <summary>
    /// Binds an observed item only to a unique explicit catalog member and never resolves that platform path as a tenant file.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_a_unique_explicit_catalog_member_can_bind_an_actual_item(bool ambiguous)
    {
        var client = Items(new object[] { new { Id = 5, ContentTypeId = "0x01" } }, form: "/_layouts/15/listform.aspx?PageType=4&ID=5");
        var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(catalog: Catalog(ambiguous)), client).AcquireAsync(Observations(), found.Publish);
        if (ambiguous) { Assert.Empty(found.Bindings); Assert.Contains(found.Evidence, e => e.ErrorCodes == "platform_catalog_alias_ambiguous"); }
        else { Assert.Single(found.Bindings); Assert.Equal(ClassicAspxIdentityKinds.PlatformCatalog, Assert.Single(found.Inventory).IdentityKind); Assert.Empty(client.Resolutions); }
    }

    /// <summary>
    /// Does not publish acquisition bindings or inventory for catalog entries that no observed item references.
    /// </summary>
    [Fact]
    public async Task Unreferenced_catalog_members_never_make_an_item_binding()
    {
        var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(catalog: Catalog()), new Client()).AcquireAsync(Observations(), found.Publish);
        Assert.Empty(found.Bindings); Assert.Empty(found.Inventory);
    }

    /// <summary>
    /// Uses a validated site-root client for shared layout metadata and bytes, preserving item scope or recording root unavailability.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Same_sc_parent_layout_uses_root_for_metadata_and_source_without_changing_item_web(bool rootAvailable)
    {
        const string layout = "/sites/fixture/_catalogs/masterpage/shared-parent.aspx";
        Guid layoutId = Guid.Parse("60000000-0000-0000-0000-000000000001");
        var child = Items(new object[] { new { Id = 2, ContentTypeId = "0x01", PublishingPageLayout = new { Url = "https://tenant.invalid" + layout } },
            new { Id = 3, ContentTypeId = "0x01", PublishingPageLayout = new { Url = "https://tenant.invalid" + layout } } });
        child.Resolver = path => path == layout ? new(DiscoveryTerminalOutcome.Failed, null, null, path, null,
            "child API HTTP400", "locator_resolution_failed", ErrorStage: "FileRequest", ErrorDetail: "System.ArgumentException:-2147024809") : File(Renderer, path);
        var root = new Client { ActualWebUrl = SiteUrl, Resolver = path => File(layoutId, path) };
        var factory = new Factory(root); var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(), child, rootAvailable ? factory : null).AcquireAsync(Observations(), found.Publish);
        var layoutPairs = found.Bindings.Where(b => b.BindingKinds.HasFlag(ClassicAspxBindingKinds.PublishingPageLayout)).ToArray();
        Assert.DoesNotContain(layout, child.Resolutions); Assert.DoesNotContain(layout, child.ContentPaths);
        if (rootAvailable)
        {
            Assert.Equal(2, layoutPairs.Length); Assert.Equal(new[] { 2, 3 }, layoutPairs.Select(b => b.ItemId).OrderBy(i => i));
            Assert.All(layoutPairs, b => { Assert.Equal(Web, b.WebId); Assert.Equal(Site, b.SiteCollectionId); Assert.Equal(ClassicAspxInventory.CreateTenantFileKey(Site, layoutId), b.AspxKey); });
            var actual = Assert.Single(found.Inventory.Where(i => i.FileUniqueId == layoutId));
            Assert.Null(actual.WebId); Assert.Equal("Example.ActualHandler", actual.HandlerEffectiveType);
            Assert.Single(root.Resolutions); Assert.Equal(layout, root.ContentPaths.Single());
            Assert.Equal(SiteUrl, factory.Requests.Single()); Assert.False(factory.Disposed);
        }
        else
        {
            Assert.Empty(layoutPairs); Assert.Empty(root.Resolutions); Assert.Empty(root.ContentPaths);
            Assert.Contains(found.Evidence, e => e.ErrorCodes == "same_site_root_client_unavailable" && e.Url.EndsWith("shared-parent.aspx"));
        }
    }

    /// <summary>
    /// Rejects foreign-authority and other-collection layouts before requesting a root client, metadata, or source bytes.
    /// </summary>
    [Theory]
    [InlineData("https://foreign.invalid/sites/fixture/layout.aspx")]
    [InlineData("/sites/other-collection/layout.aspx")]
    public async Task Foreign_or_wrong_collection_layout_never_fetches_metadata_or_bytes(string layout)
    {
        var child = Items(new object[] { new { Id = 2, ContentTypeId = "0x01", PublishingPageLayout = new { Url = layout } } });
        var root = new Client { ActualWebUrl = SiteUrl }; var factory = new Factory(root); var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(), child, factory).AcquireAsync(Observations(), found.Publish);
        Assert.Empty(factory.Requests); Assert.Empty(root.Resolutions); Assert.Empty(root.ContentPaths);
        Assert.DoesNotContain(found.Bindings, b => b.BindingKinds.HasFlag(ClassicAspxBindingKinds.PublishingPageLayout));
    }

    /// <summary>
    /// Rejects a factory client whose actual web is not the expected site root and records the scope mismatch.
    /// </summary>
    [Fact]
    public async Task Factory_returning_wrong_web_is_not_used_as_site_root()
    {
        var child = Items(new object[] { new { Id = 2, ContentTypeId = "0x01", PublishingPageLayout = new { Url = "/sites/fixture/_catalogs/masterpage/a.aspx" } } });
        var wrong = new Client { ActualWebUrl = new Uri("https://tenant.invalid/sites/not-the-collection") };
        var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(), child, new Factory(wrong)).AcquireAsync(Observations(), found.Publish);
        Assert.Empty(wrong.Resolutions); Assert.Empty(wrong.ContentPaths);
        Assert.Contains(found.Evidence, e => e.ErrorCodes == "same_site_root_client_scope_mismatch");
    }

    /// <summary>
    /// Keeps separate target evidence for equal layout failures while retaining safe diagnostics and removing bearer secrets.
    /// </summary>
    [Fact]
    public async Task Equal_failure_codes_for_different_layout_paths_keep_distinct_safe_target_evidence()
    {
        const string a = "/sites/fixture/_catalogs/masterpage/a.aspx", b = "/sites/fixture/_catalogs/masterpage/b.aspx";
        var child = Items(new object[] { new { Id = 2, ContentTypeId = "0x01", PublishingPageLayout = new { Url = a } },
            new { Id = 3, ContentTypeId = "0x01", PublishingPageLayout = new { Url = b } } });
        var root = new Client { ActualWebUrl = SiteUrl, Resolver = path => new(DiscoveryTerminalOutcome.Denied,
            null, null, path, null, "GET source metadata", "locator_denied", ErrorStage: "MetadataRequest",
            ErrorDetail: "ServerException:-2147024891 Bearer synthetic-secret") };
        var found = new Collected();
        await new ClassicAspxWebAcquisition(Context(), child, new Factory(root)).AcquireAsync(Observations(), found.Publish);
        var gaps = found.Evidence.Where(e => e.ErrorCodes == "locator_denied").ToArray();
        Assert.Equal(2, gaps.Length); Assert.Equal(2, gaps.Select(e => e.RecordKey).Distinct().Count());
        Assert.Contains(gaps, e => e.Url.EndsWith("/a.aspx")); Assert.Contains(gaps, e => e.Url.EndsWith("/b.aspx"));
        Assert.All(gaps, e => { Assert.Equal("MetadataRequest", e.ErrorStage); Assert.Equal("Denied", e.DiscoveryStatus); Assert.DoesNotContain("synthetic-secret", e.ErrorDetail); Assert.Contains("ServerException:-2147024891", e.ErrorDetail); Assert.Contains("ReferencePath", e.EvidenceJson); });
    }

    /// <summary>
    /// Avoids attributing a legacy physical parent layout to the child worker or inventing a file-self item relation.
    /// </summary>
    [Fact]
    public async Task Legacy_physical_parent_layout_never_invents_worker_ownership_or_item_self()
    {
        var client = new Client(); var found = new Collected();
        var row = new ClassicPageDiscovery { ScanId = Scan, RowType = "Page", ScopeType = "File", SiteCollectionId = Site,
            WebId = Web, ListId = Guid.NewGuid(), ListItemId = 99, FileUniqueId = Renderer,
            Url = "/sites/fixture/_catalogs/masterpage/parent-layout.aspx" };
        await new ClassicAspxWebAcquisition(Context(), client).AcquireAsync(Observations(row), found.Publish);
        Assert.Null(Assert.Single(found.Inventory).WebId);
        Assert.Empty(found.Bindings);
    }

    /// <summary>
    /// Rejects redirects, response-URI drift, and oversized JSON responses after one send without returning successful empty pages.
    /// </summary>
    [Theory]
    [InlineData("redirect")]
    [InlineData("drift")]
    [InlineData("size")]
    public async Task Bounded_json_GET_never_follows_redirect_or_accepts_unbounded_page(string fault)
    {
        var handler = new Handler((r, _) =>
        {
            var response = Reply(r, fault == "redirect" ? HttpStatusCode.Found : HttpStatusCode.OK,
                Encoding.UTF8.GetBytes("{\"value\":[]}"), "application/json");
            if (fault == "drift") response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://foreign.invalid/_api/web/lists");
            if (fault == "size") response.Content.Headers.ContentLength = 100;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var page = await PnPContextSharePointAspxRestClient.ReadBoundedPageAsync(http, WebUrl,
            new Uri(WebUrl + "/_api/web/lists"), Authenticate, maxBytes: 64);
        Assert.True(page.Outcome is DiscoveryTerminalOutcome.Unknown or DiscoveryTerminalOutcome.Truncated);
        Assert.Empty(page.Items); Assert.Equal(1, handler.Sends);
    }

    /// <summary>
    /// Applies the JSON deadline through body consumption and records timeout truncation rather than empty success.
    /// </summary>
    [Fact]
    public async Task Bounded_json_deadline_includes_body_and_preserves_no_empty_success()
    {
        var handler = new Handler((r, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = r, Content = new SlowBody() };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var page = await PnPContextSharePointAspxRestClient.ReadBoundedPageAsync(http, WebUrl,
            new Uri(WebUrl + "/_api/web/lists"), Authenticate, timeout: TimeSpan.FromMilliseconds(30));
        Assert.Equal(DiscoveryTerminalOutcome.Truncated, page.Outcome);
        Assert.Equal("json_response_timeout", page.ErrorCode); Assert.Empty(page.Items);
    }

    private sealed class SlowBody : HttpContent
    {
        /// <summary>
        /// Leaves response length unknown so bounded readers must enforce their limits while consuming the stream.
        /// </summary>
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        /// <summary>
        /// Rejects serialization because this fake content is consumed only through its deliberately stalled read stream.
        /// </summary>
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => throw new NotSupportedException();
        /// <summary>
        /// Creates the cancellation-aware stalled stream used to test deadlines after headers have already succeeded.
        /// </summary>
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new SlowRead());
        private sealed class SlowRead : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            /// <summary>
            /// Rejects flushing because the stalled body is a read-only asynchronous test stream.
            /// </summary>
            public override void Flush() => throw new NotSupportedException();
            /// <summary>
            /// Rejects synchronous reads so deadline tests must use the cancellation-aware asynchronous path.
            /// </summary>
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            /// <summary>
            /// Waits indefinitely until the reader cancels, isolating whether its deadline covers body consumption.
            /// </summary>
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            { await Task.Delay(Timeout.Infinite, ct); return 0; }
            /// <summary>
            /// Rejects seeking because the fake response body is forward-only.
            /// </summary>
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            /// <summary>
            /// Rejects resizing because the response stream exposes no writable storage.
            /// </summary>
            public override void SetLength(long value) => throw new NotSupportedException();
            /// <summary>
            /// Rejects writes because the response body exists only to stall asynchronous reads.
            /// </summary>
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Requires a successful binary header response with a stalled body to time out as incomplete source after one request.
    /// </summary>
    [Fact]
    public async Task The_same_deadline_bounds_body_after_successful_headers()
    {
        var handler = new Handler((r, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = r, Content = new SlowBody() };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var result = await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromMilliseconds(30), Authenticate);
        Assert.Equal("file_source_timeout", result.ErrorCode);
        Assert.Equal(AspxHandlerReadState.Truncated, result.ReadState);
        Assert.False(result.IsComplete); Assert.Equal(1, handler.Sends);
    }

    /// <summary>
    /// Runs a supplied in-memory HTTP scenario and counts sends so redirect and timeout tests can enforce request boundaries.
    /// </summary>
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Sends;
        /// <summary>
        /// Counts HTTP sends before forwarding the exact request and cancellation token to the scenario callback.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Sends++; return send(request, ct); }
    }
    /// <summary>
    /// Creates a response tied to the original request with caller-selected status, raw bytes, and optional media type.
    /// </summary>
    private static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode status, byte[] bytes, string media = "application/octet-stream")
    {
        var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        if (media != null) response.Content.Headers.ContentType = new MediaTypeHeaderValue(media);
        return response;
    }
    /// <summary>
    /// Adds a synthetic bearer header after checking cancellation, without accessing any live authentication service.
    /// </summary>
    private static Task Authenticate(HttpRequestMessage request, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-only"); return Task.CompletedTask; }

    /// <summary>
    /// Requires an authenticated raw-value request and complete same-authority bytes before the parser can infer a source-level fallback.
    /// </summary>
    [Fact]
    public async Task Binary_reader_uses_authenticated_value_api_and_strict_complete_source()
    {
        var handler = new Handler((r, _) => { Assert.Equal("Bearer", r.Headers.Authorization.Scheme); Assert.EndsWith("/$value", r.RequestUri.AbsolutePath); return Task.FromResult(Reply(r, HttpStatusCode.OK, Encoding.UTF8.GetBytes("<%@ Page %>"))); });
        using var http = new HttpClient(handler);
        var result = await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromSeconds(1), Authenticate);
        Assert.True(result.IsComplete); Assert.True(result.SameAuthorityConfirmed); Assert.Equal(AspxHandlerSourceKind.ActualFileBytes, result.Source);
        Assert.Equal(AspxHandlerParseState.NoInherits, AspxHandlerParser.Parse(result.Bytes, result.Source, result.IsComplete, result.ReadState).ParseState);
        Assert.Equal(1, handler.Sends);
    }

    /// <summary>
    /// Refuses handler defaults for authentication failures, missing files, partial responses, and redirects despite plausible body bytes.
    /// </summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(206)]
    [InlineData(302)]
    public async Task Binary_http_failures_partial_and_redirect_are_never_defaultable(int status)
    {
        var handler = new Handler((r, _) => Task.FromResult(Reply(r, (HttpStatusCode)status, Encoding.UTF8.GetBytes("<%@ Page %>"))));
        using var http = new HttpClient(handler);
        var result = await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromSeconds(1), Authenticate);
        Assert.False(result.IsComplete); Assert.NotEqual(AspxHandlerSourceKind.ActualFileBytes, result.Source);
        Assert.Null(AspxHandlerParser.Parse(result.Bytes, result.Source, result.IsComplete, result.ReadState).EffectiveInherits);
        Assert.Equal(1, handler.Sends);
    }

    /// <summary>
    /// Prevents rendered, JSON, or untyped bodies from being promoted to actual-source evidence or a framework default.
    /// </summary>
    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData(null)]
    public async Task Rendered_or_unproven_media_never_becomes_a_framework_default(string media)
    {
        var handler = new Handler((r, _) => Task.FromResult(Reply(r, HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html>rendered</html>"), media)));
        using var http = new HttpClient(handler);
        var result = await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromSeconds(1), Authenticate);
        Assert.NotEqual(AspxHandlerSourceKind.ActualFileBytes, result.Source);
        Assert.Null(AspxHandlerParser.Parse(result.Bytes, result.Source, result.IsComplete, result.ReadState).EffectiveInherits);
    }

    /// <summary>
    /// Enforces the byte cap and declared body length, retaining truncation or incomplete-transfer diagnostics.
    /// </summary>
    [Fact]
    public async Task Binary_size_and_length_completion_are_enforced()
    {
        var handler = new Handler((r, _) => Task.FromResult(Reply(r, HttpStatusCode.OK, new byte[33])));
        using var http = new HttpClient(handler);
        var result = await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 32, TimeSpan.FromSeconds(1), Authenticate);
        Assert.Equal(DiscoveryTerminalOutcome.Truncated, result.Outcome); Assert.Empty(result.Bytes);
        var wrong = new Handler((r, _) => { var reply = Reply(r, HttpStatusCode.OK, new byte[8]); reply.Content.Headers.ContentLength = 9; return Task.FromResult(reply); });
        using var http2 = new HttpClient(wrong);
        Assert.Equal("file_source_incomplete_body", (await SharePointFileContentReader.ReadAsync(http2, WebUrl, Form, 32, TimeSpan.FromSeconds(1), Authenticate)).ErrorCode);
    }

    /// <summary>
    /// Rejects response-URI drift and foreign locators without following another authority or issuing a second request.
    /// </summary>
    [Fact]
    public async Task Response_uri_drift_and_foreign_paths_are_not_followed_or_sent()
    {
        var handler = new Handler((r, _) => { var response = Reply(r, HttpStatusCode.OK, new byte[0]); response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://foreign.invalid/error"); return Task.FromResult(response); });
        using var http = new HttpClient(handler);
        Assert.Equal("file_source_redirect_or_uri_drift", (await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromSeconds(1), Authenticate)).ErrorCode);
        await Assert.ThrowsAsync<ArgumentException>(() => SharePointFileContentReader.ReadAsync(http, WebUrl, "//foreign.invalid/a.aspx", 1024, TimeSpan.FromSeconds(1), Authenticate));
        Assert.Equal(1, handler.Sends);
    }

    /// <summary>
    /// Includes authentication in the total read deadline and lets caller cancellation propagate without an HTTP send.
    /// </summary>
    [Fact]
    public async Task One_deadline_also_bounds_auth_and_caller_cancellation_propagates()
    {
        var handler = new Handler((r, _) => Task.FromResult(Reply(r, HttpStatusCode.OK, new byte[0])));
        using var http = new HttpClient(handler);
        var result = await SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromMilliseconds(20),
            async (_, ct) => await Task.Delay(Timeout.Infinite, ct));
        Assert.Equal("file_source_timeout", result.ErrorCode); Assert.Equal(0, handler.Sends);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SharePointFileContentReader.ReadAsync(http, WebUrl, Form, 1024, TimeSpan.FromSeconds(1), Authenticate, canceled.Token));
    }
}
