using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using System.Net;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

/// <summary>Proves physical folder authority, overlapping-root evidence and declaration-only enrichment without live SharePoint calls.</summary>
[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class SharePointLiveAspxDiscoveryProviderTests
{
    private static readonly Uri WebUrl = new("https://contoso.sharepoint.com/sites/a");
    private static readonly Guid ListId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FolderId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string RootPath = "/sites/a/Docs";

    /// <summary>Every observed list root is admitted regardless of classification metadata, hidden state or catalog/page template.</summary>
    [Theory]
    [InlineData(0, 100, false, false)]
    [InlineData(1, 101, true, false)]
    [InlineData(1, 116, true, true)]
    [InlineData(1, 119, false, false)]
    [InlineData(1, 850, false, false)]
    [InlineData(2, 600, true, false)]
    [InlineData(null, 100, false, false)]
    public async Task Classification_does_not_filter_authorized_physical_roots(int? baseType, int template, bool hidden, bool catalog)
    {
        using var factory = new FolderFactory();
        factory.Client.Lists = new[] { ListRow(RootPath, baseType, template, hidden, catalog) };
        factory.Client.Files[RootPath] = new[] { FileRow(1, RootPath + "/page.aspx") };
        using var provider = Provider(factory);

        var result = await TraverseAsync(provider);

        result.Records.Should().ContainSingle(record => record.PhysicalLocator == RootPath + "/page.aspx" && record.ListId == ListId);
        factory.Client.Requests.Should().NotContain(uri => uri.AbsolutePath.EndsWith("/items", StringComparison.OrdinalIgnoreCase));
        factory.Client.Resolutions.Should().Be(0);
    }

    /// <summary>Web-root, list-root and explicit Forms entrances retain distinct parent scopes but share each complete physical read and file fact.</summary>
    [Fact]
    public async Task Overlapping_roots_preserve_parent_evidence_without_duplicate_requests_or_files()
    {
        using var factory = new FolderFactory();
        factory.Client.Files[RootPath] = new[] { FileRow(1, RootPath + "/root.aspx") };
        factory.Client.Files[RootPath + "/Forms"] = new[] { FileRow(2, RootPath + "/Forms/DispForm.aspx") };
        factory.Client.Folders[RootPath] = new[] { FolderRow(RootPath + "/Forms"), FolderRow(RootPath + "/Deep") };
        factory.Client.Folders[RootPath + "/Deep"] = new[] { FolderRow(RootPath + "/Deep/Deeper") };
        factory.Client.Files[RootPath + "/Deep/Deeper"] = new[] { FileRow(3, RootPath + "/Deep/Deeper/custom.aspx") };
        using var provider = Provider(factory);

        var result = await TraverseAsync(provider);

        result.Scopes.GroupBy(scope => scope.ScopeKey).Should().OnlyContain(group => group.Count() == 1);
        result.Scopes.Where(scope => scope.Locator == RootPath + "/Forms").Select(scope => scope.ParentScopeKey)
            .Distinct().Should().HaveCountGreaterThan(1);
        result.Records.Should().HaveCount(3).And.OnlyContain(record => record.ListId == ListId);
        result.Records.Select(record => record.FileUniqueId).Should().OnlyHaveUniqueItems();
        foreach (var group in factory.Client.Requests.Where(IsFolderCollection).GroupBy(uri => uri.AbsoluteUri))
        {
            group.Should().ContainSingle();
        }
        var reused = provider.ReferenceCollector.ReadSurfaceEvidence().Where(row => row.ActualMethod == "CACHE").ToArray();
        reused.Should().NotBeEmpty().And.OnlyContain(row => row.ParentScopeKey != null &&
            row.EvidenceRefs.Any(value => value.StartsWith("reused-surface=", StringComparison.Ordinal)));
        foreach (var row in reused)
        {
            var sourceId = row.EvidenceRefs.Single(value => value.StartsWith("reused-surface=", StringComparison.Ordinal))[15..];
            provider.ReferenceCollector.ReadSurfaceEvidence().Should().Contain(source => source.SurfaceId == sourceId &&
                source.PaginationChainHash == row.PaginationChainHash);
            provider.ReferenceCollector.ReadPaginationEvidence().Should().Contain(receipt => receipt.CollectionScopeKey == sourceId);
        }
    }

    /// <summary>Both folder collections drain every continuation before aliases may reuse their successful coverage proof.</summary>
    [Fact]
    public async Task Files_and_folders_pagination_is_retained_and_reused_without_reissuing_HTTP()
    {
        using var factory = new FolderFactory();
        factory.Client.Files[RootPath] = new[] { FileRow(1, RootPath + "/first.aspx"), FileRow(2, RootPath + "/second.aspx") };
        factory.Client.Folders[RootPath] = new[] { FolderRow(RootPath + "/One"), FolderRow(RootPath + "/Two") };
        factory.Client.PageRootCollections = true;
        using var provider = Provider(factory);

        var result = await TraverseAsync(provider);

        result.Records.Should().HaveCount(2);
        factory.Client.Requests.Count(uri => FolderPath(uri) == RootPath && uri.AbsolutePath.EndsWith("/Files")).Should().Be(2);
        factory.Client.Requests.Count(uri => FolderPath(uri) == RootPath && uri.AbsolutePath.EndsWith("/Folders")).Should().Be(2);
        var paged = provider.ReferenceCollector.ReadPaginationEvidence().GroupBy(receipt => receipt.CollectionScopeKey)
            .Where(group => group.Count() == 2).ToArray();
        paged.Should().HaveCount(2);
        foreach (var receipts in paged)
        {
            var ordered = receipts.OrderBy(receipt => receipt.PageOrdinal).ToArray();
            ordered[0].NextTokenHash.Should().Be(ordered[1].RequestTokenHash);
            ordered[1].TerminalFlag.Should().BeTrue();
            AspxPaginationContract.Validate(ordered, DiscoveryTerminalOutcome.Complete).GapCodes.Should().BeEmpty();
        }
    }

    /// <summary>Forms, Views, default-display, CT-display and full-inventory welcome locators are references even if file resolution could succeed.</summary>
    [Fact]
    public async Task Metadata_only_references_never_mint_physical_inventory()
    {
        using var factory = new FolderFactory();
        factory.Client.ReferencePath = RootPath + "/metadata-only.aspx";
        factory.Client.WelcomePage = "Docs/metadata-only.aspx";
        using var provider = Provider(factory);

        var result = await TraverseAsync(provider);

        result.Records.Should().BeEmpty();
        factory.Client.Resolutions.Should().Be(0);
        var references = provider.ReferenceCollector.ReadReferenceEvidence();
        references.Should().Contain(reference => reference.SourceKind == AspxReferenceSourceKinds.ListForm);
        references.Should().Contain(reference => reference.SourceKind == AspxReferenceSourceKinds.ListView);
        references.Should().Contain(reference => reference.SourceKind == AspxReferenceSourceKinds.ListDefaultDisplayForm);
        references.Should().Contain(reference => reference.SourceKind == AspxReferenceSourceKinds.ContentTypeDisplayForm && reference.ContentTypeId == "0x0101");
        references.Should().Contain(reference => reference.SourceKind == AspxReferenceSourceKinds.WebWelcomePage);
        references.Should().OnlyContain(reference => reference.LinkedFileUniqueId == null && reference.Disposition == AspxReferenceDispositions.Declared);
    }

    /// <summary>Optional role metadata failures retain denied evidence without preventing the independent physical folder census.</summary>
    [Fact]
    public async Task Optional_metadata_failure_does_not_block_physical_folder_census()
    {
        using var factory = new FolderFactory();
        factory.Client.DenyExpandedLists = true;
        factory.Client.DenyReferences = true;
        factory.Client.ThrowWelcome = true;
        factory.Client.Files[RootPath] = new[] { FileRow(1, RootPath + "/physical.aspx") };
        using var provider = Provider(factory);

        var result = await TraverseAsync(provider);

        result.Records.Should().ContainSingle(record => record.PhysicalLocator == RootPath + "/physical.aspx");
        provider.ReferenceCollector.ReadSurfaceEvidence().Should().Contain(row => row.RequiredAdapter == "AllListsAuthority" && row.TerminalOutcome == DiscoveryTerminalOutcome.Denied);
        provider.ReferenceCollector.ReadSurfaceEvidence().Should().Contain(row => row.RequiredAdapter == "PhysicalListsAuthorityFallback" && row.TerminalOutcome == DiscoveryTerminalOutcome.Complete);
        provider.ReferenceCollector.ReadReferenceEvidence().Should().Contain(row => row.SourceKind == AspxReferenceSourceKinds.WebWelcomePage && row.Disposition == AspxReferenceDispositions.ReferenceUnavailable);
        provider.ReferenceCollector.ReadSurfaceEvidence().Should().Contain(row => row.RequiredAdapter == "AllListViewsAuthority" && row.TerminalOutcome == DiscoveryTerminalOutcome.Denied);
    }

    /// <summary>The explicit Forms entry uses its observed physical folder GUID, not the owning list root GUID.</summary>
    [Fact]
    public async Task Forms_folder_identity_is_observed_independently_of_list_root_identity()
    {
        var formsId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        using var factory = new FolderFactory();
        factory.Client.Folders[RootPath] = new[] { JsonSerializer.SerializeToElement(new
        {
            UniqueId = formsId, Name = "Forms", ServerRelativeUrl = RootPath + "/Forms",
        }) };
        factory.Client.Files[RootPath + "/Forms"] = new[] { FileRow(1, RootPath + "/Forms/DispForm.aspx") };
        using var provider = Provider(factory);

        var result = await TraverseAsync(provider);

        result.Records.Should().ContainSingle(record => record.ListId == ListId && record.FolderUniqueId == formsId);
    }

    /// <summary>A failed expanded file projection may fall back to physical columns without requiring ListItemAllFields.</summary>
    [Fact]
    public async Task Per_file_optional_metadata_failure_has_a_physical_authority_fallback()
    {
        using var factory = new FolderFactory();
        factory.Client.DenyExpandedFiles = true;
        factory.Client.Files[RootPath] = new[] { FileRow(1, RootPath + "/physical.aspx") };
        using var provider = Provider(factory);

        var result = await TraverseAsync(provider);

        result.Records.Should().ContainSingle();
        provider.ReferenceCollector.ReadSurfaceEvidence().Should().Contain(row => row.RequiredAdapter == "PhysicalFolderFilesAuthorityFallback" && row.TerminalOutcome == DiscoveryTerminalOutcome.Complete);
    }

    /// <summary>Denied folder reads remain unknown contents and cannot be reused as successful coverage by another entrance.</summary>
    [Fact]
    public async Task Denied_folder_authority_is_not_cached_as_complete()
    {
        using var factory = new FolderFactory();
        factory.Client.DenyFolderFiles = true;
        using var provider = Provider(factory);

        var result = await TraverseAsync(provider);

        result.Records.Should().BeEmpty();
        factory.Client.Requests.Count(uri => FolderPath(uri) == RootPath && uri.AbsolutePath.EndsWith("/Files")).Should().BeGreaterThan(2);
        provider.ReferenceCollector.ReadSurfaceEvidence().Where(row => row.ActualEndpoint.Contains("/Files", StringComparison.Ordinal) && row.TerminalOutcome == DiscoveryTerminalOutcome.Denied)
            .Should().NotBeEmpty().And.OnlyContain(row => row.ExpectedCount == null && row.ExpectedCountState == AspxExpectedCountState.Unknown);
    }

    /// <summary>A physical ASPX row without File.UniqueId produces a gap, never a path-derived or empty GUID-backed file fact.</summary>
    [Fact]
    public async Task Missing_file_GUID_is_a_gap_not_a_physical_fact()
    {
        using var factory = new FolderFactory();
        factory.Client.Files[RootPath] = new[] { JsonSerializer.SerializeToElement(new { Name = "missing.aspx", ServerRelativeUrl = RootPath + "/missing.aspx" }) };
        using var provider = Provider(factory);
        var root = await ListRootAsync(provider);

        var batches = await ReadBatchesAsync(provider, root);

        batches.SelectMany(batch => batch.Records).Should().BeEmpty();
        batches.Last().TerminalOutcome.Should().Be(DiscoveryTerminalOutcome.Unknown);
        provider.ReferenceCollector.ReadGaps().Should().Contain(gap => gap.EndsWith(":file_unique_id_missing_or_invalid", StringComparison.Ordinal));
    }

    /// <summary>Cancellation after one page preserves that receipt and outstanding continuation without manufacturing a terminal success.</summary>
    [Fact]
    public async Task Cancellation_keeps_partial_pagination_evidence_and_does_not_cache_coverage()
    {
        using var cancellation = new CancellationTokenSource();
        using var factory = new FolderFactory();
        factory.Client.Files[RootPath] = new[] { FileRow(1, RootPath + "/one.aspx"), FileRow(2, RootPath + "/two.aspx") };
        factory.Client.PageRootCollections = true;
        factory.Client.CancelOnSecondFilePage = cancellation;
        using var provider = Provider(factory);
        var root = await ListRootAsync(provider);

        var action = () => ReadBatchesAsync(provider, root, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();

        provider.ReferenceCollector.ReadPaginationEvidence().Should().Contain(receipt => receipt.CollectionScopeKey == "files:" + root.ScopeKey && !receipt.TerminalFlag);
        provider.ReferenceCollector.ReadSurfaceEvidence().Should().Contain(row => row.SurfaceId == "files:" + root.ScopeKey && row.TerminalOutcome == DiscoveryTerminalOutcome.Cancelled && row.ContinuationRemaining);
        factory.Client.CancelOnSecondFilePage = null;
        var retried = await ReadBatchesAsync(provider, root);
        retried.SelectMany(batch => batch.Records).Should().HaveCount(2);
    }

    /// <summary>A truncated folder response retains uncertainty and is reread rather than reused as a successful census.</summary>
    [Fact]
    public async Task Truncated_folder_collection_cannot_authorize_cache_reuse()
    {
        using var factory = new FolderFactory();
        factory.Client.TruncateFolderFiles = true;
        using var provider = Provider(factory);
        var root = await ListRootAsync(provider);

        var first = await ReadBatchesAsync(provider, root);
        await ReadBatchesAsync(provider, root);

        first.Last().TerminalOutcome.Should().Be(DiscoveryTerminalOutcome.Truncated);
        factory.Client.Requests.Count(uri => FolderPath(uri) == RootPath && uri.AbsolutePath.EndsWith("/Files")).Should().Be(2);
    }

    /// <summary>Creates one provider-bound Web with real scope identities and no optional shared snapshot.</summary>
    private static SharePointLiveAspxDiscoveryProvider Provider(FolderFactory factory) => new(
        new SharePointLiveAspxDiscoveryOptions("test", "authorized-web", "folder-test-v1", new string('a', 64)),
        factory, new AspxWebAcquisitionContext(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), WebUrl,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), WebUrl, "/sites/a", "STS#3"));

    /// <summary>Reads the list's physical root directly so failure and cancellation assertions have a stable scope owner.</summary>
    private static async Task<DiscoveryScopeRegistration> ListRootAsync(SharePointLiveAspxDiscoveryProvider provider)
    {
        var list = (await provider.EnumerateChildrenAsync(provider.RootScope)).ObservedChildren.Single(scope => scope.Metadata["role"] == "list");
        return (await provider.EnumerateChildrenAsync(list)).ObservedChildren.Single(scope => scope.Metadata["role"] == "folder");
    }

    /// <summary>Traverses the existing provider scope tree and fails immediately if any scope is scheduled twice.</summary>
    private static async Task<(List<DiscoveryScopeRegistration> Scopes, List<RawDiscoveryRecord> Records)> TraverseAsync(SharePointLiveAspxDiscoveryProvider provider)
    {
        var scopes = new List<DiscoveryScopeRegistration>();
        var records = new List<RawDiscoveryRecord>();
        var queue = new Queue<DiscoveryScopeRegistration>();
        var scheduled = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(provider.RootScope);
        while (queue.TryDequeue(out var scope))
        {
            scheduled.Add(scope.ScopeKey).Should().BeTrue("the existing engine requires a single parent for each scope key");
            scopes.Add(scope);
            if (scope.SourceKind != null)
            {
                records.AddRange((await ReadBatchesAsync(provider, scope)).SelectMany(batch => batch.Records));
            }
            foreach (var child in (await provider.EnumerateChildrenAsync(scope)).ObservedChildren)
            {
                child.ParentScopeKey.Should().Be(scope.ScopeKey);
                queue.Enqueue(child);
            }
        }
        return (scopes, records);
    }

    /// <summary>Drains one surface without hiding terminal batch evidence behind a file-only helper.</summary>
    private static async Task<List<RawDiscoveryBatch>> ReadBatchesAsync(SharePointLiveAspxDiscoveryProvider provider,
        DiscoveryScopeRegistration scope, CancellationToken cancellationToken = default)
    {
        var batches = new List<RawDiscoveryBatch>();
        await foreach (var batch in provider.CreateRawSource(scope).ReadBatchesAsync(cancellationToken))
        {
            batches.Add(batch);
        }
        return batches;
    }

    /// <summary>Builds a list authority row with declaration metadata unrelated to physical admission.</summary>
    private static JsonElement ListRow(string path, int? baseType = 0, int template = 100, bool hidden = false, bool catalog = false) =>
        JsonSerializer.SerializeToElement(new
        {
            Id = ListId, Title = "Root", BaseType = baseType, BaseTemplate = template, Hidden = hidden, IsCatalog = catalog,
            RootFolder = new { ServerRelativeUrl = path, UniqueId = FolderId },
            DefaultDisplayFormUrl = RootPath + "/metadata-only.aspx",
            ContentTypes = new[] { new { Id = new { StringValue = "0x0101" }, DisplayFormUrl = "Docs/metadata-only.aspx" } },
        });

    /// <summary>Builds a real file identity independently of its path so no path-to-GUID behavior is tested accidentally.</summary>
    private static JsonElement FileRow(int id, string path) => JsonSerializer.SerializeToElement(new
    {
        UniqueId = Guid.Parse("cccccccc-cccc-cccc-cccc-" + id.ToString("D12")),
        Name = path[(path.LastIndexOf('/') + 1)..], ServerRelativeUrl = path, CustomizedPageStatus = "Uncustomized",
    });

    /// <summary>Builds an observed folder row; scope identity remains parent-bound instead of being inferred from this GUID.</summary>
    private static JsonElement FolderRow(string path) => JsonSerializer.SerializeToElement(new
    {
        UniqueId = FolderId, Name = path[(path.LastIndexOf('/') + 1)..], ServerRelativeUrl = path,
    });

    /// <summary>Recognizes only raw Files/Folders collection endpoints in request de-duplication assertions.</summary>
    private static bool IsFolderCollection(Uri uri) => uri.AbsolutePath.EndsWith("/Files", StringComparison.Ordinal) || uri.AbsolutePath.EndsWith("/Folders", StringComparison.Ordinal);

    /// <summary>Extracts the decoded REST resource-path argument without changing the product's URI builder.</summary>
    private static string FolderPath(Uri uri)
    {
        var query = WebUtility.UrlDecode(uri.Query);
        var start = query.IndexOf("@u='", StringComparison.Ordinal);
        if (start < 0) return null;
        start += 4;
        var end = query.IndexOf("'&", start, StringComparison.Ordinal);
        return end < 0 ? null : query[start..end];
    }

    private sealed class FolderFactory : ISharePointAspxRestClientFactory
    {
        internal FolderClient Client { get; } = new();
        /// <summary>Returns the single owned fake transport for this Web.</summary>
        public Task<ISharePointAspxRestClient> GetAsync(Uri webUrl, CancellationToken cancellationToken = default) => Task.FromResult<ISharePointAspxRestClient>(Client);
        /// <summary>Closes this no-resource fixture without sharing transport ownership with another provider.</summary>
        public void Dispose() { }
    }

    private sealed class FolderClient : ISharePointAspxRestClient
    {
        public Uri WebUrl => SharePointLiveAspxDiscoveryProviderTests.WebUrl;
        internal List<Uri> Requests { get; } = new();
        internal IReadOnlyList<JsonElement> Lists { get; set; } = new[] { ListRow(RootPath) };
        internal Dictionary<string, JsonElement[]> Files { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, JsonElement[]> Folders { get; } = new(StringComparer.Ordinal);
        internal string ReferencePath { get; set; }
        internal string WelcomePage { get; set; }
        internal bool DenyExpandedLists { get; set; }
        internal bool DenyExpandedFiles { get; set; }
        internal bool DenyReferences { get; set; }
        internal bool ThrowWelcome { get; set; }
        internal bool DenyFolderFiles { get; set; }
        internal bool TruncateFolderFiles { get; set; }
        internal bool PageRootCollections { get; set; }
        internal CancellationTokenSource CancelOnSecondFilePage { get; set; }
        internal int Resolutions { get; private set; }

        /// <summary>Serves deterministic physical and metadata pages, including independent denial, truncation and cancellation cases.</summary>
        public Task<SharePointRestPage> GetPageAsync(Uri requestUri, CancellationToken cancellationToken = default)
        {
            Requests.Add(requestUri);
            var items = (IReadOnlyList<JsonElement>)Array.Empty<JsonElement>();
            var outcome = DiscoveryTerminalOutcome.Complete;
            string next = null;
            var path = requestUri.AbsolutePath;
            var folder = FolderPath(requestUri);
            var secondPage = requestUri.Query.Contains("$skiptoken=second", StringComparison.Ordinal);
            if (path.EndsWith("/lists", StringComparison.Ordinal))
            {
                if (DenyExpandedLists && requestUri.Query.Contains("ContentTypes", StringComparison.Ordinal)) outcome = DiscoveryTerminalOutcome.Denied;
                else items = Lists;
            }
            else if (IsFolderCollection(requestUri))
            {
                var files = path.EndsWith("/Files", StringComparison.Ordinal);
                if (files && secondPage && CancelOnSecondFilePage != null)
                {
                    CancelOnSecondFilePage.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (files && (DenyFolderFiles || DenyExpandedFiles && requestUri.Query.Contains("ListItemAllFields", StringComparison.Ordinal))) outcome = DiscoveryTerminalOutcome.Denied;
                else if (files && TruncateFolderFiles) outcome = DiscoveryTerminalOutcome.Truncated;
                else
                {
                    var collection = files ? Files : Folders;
                    items = folder != null && collection.TryGetValue(folder, out var rows) ? rows : Array.Empty<JsonElement>();
                    if (PageRootCollections && folder == RootPath && items.Count > 1)
                    {
                        if (secondPage) items = items.Skip(1).ToArray();
                        else
                        {
                            items = items.Take(1).ToArray();
                            next = requestUri.AbsoluteUri + "&$skiptoken=second";
                        }
                    }
                }
            }
            else if (path.EndsWith("/Forms", StringComparison.Ordinal) || path.EndsWith("/Views", StringComparison.Ordinal))
            {
                if (DenyReferences) outcome = DiscoveryTerminalOutcome.Denied;
                else if (ReferencePath != null) items = new[] { JsonSerializer.SerializeToElement(new { Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), ServerRelativeUrl = ReferencePath }) };
            }
            return Task.FromResult(new SharePointRestPage(requestUri,
                outcome == DiscoveryTerminalOutcome.Denied ? HttpStatusCode.Forbidden : HttpStatusCode.OK,
                items, next, DiscoveryHash.Of(requestUri.AbsoluteUri, outcome.ToString()), "fixture", outcome,
                outcome == DiscoveryTerminalOutcome.Denied ? "fixture_denied" : null,
                SharePointSemanticDetectorResults.None, DateTimeOffset.UtcNow));
        }

        /// <summary>Would resolve even metadata-only locators, making any forbidden inventory-time resolution observable.</summary>
        public Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        {
            Resolutions++;
            return Task.FromResult(new SharePointResolvedFile(DiscoveryTerminalOutcome.Complete,
                "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", "metadata-only.aspx", serverRelativeUrl,
                "Uncustomized", "fixture:resolve", ListId: ListId));
        }

        /// <summary>Models a welcome-page property independently from any physical file collection.</summary>
        public Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowWelcome) throw new InvalidOperationException("optional welcome metadata failed");
            return Task.FromResult(new SharePointModeledValue(DiscoveryTerminalOutcome.Complete, WelcomePage,
                "fixture", "welcome", null, "fixture:welcome", DateTimeOffset.UtcNow));
        }

        /// <summary>Exposes the list root as a web-root child to exercise overlap with the independent all-lists entry.</summary>
        public Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharePointModeledFolderResult(DiscoveryTerminalOutcome.Complete,
                "ffffffff-ffff-ffff-ffff-ffffffffffff", serverRelativeUrl,
                new[] { new SharePointModeledFolder(FolderId.ToString("D"), "Docs", RootPath) },
                Array.Empty<SharePointModeledFile>(), "fixture", "folder", null, "fixture:folder", DateTimeOffset.UtcNow));

        /// <summary>Releases no external resources because all responses are in-memory observations.</summary>
        public void Dispose() { }
    }
}
