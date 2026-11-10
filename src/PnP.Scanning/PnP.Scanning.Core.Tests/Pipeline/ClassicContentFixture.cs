using PnP.Scanning.Core.ClassicPages.Collection.Module;
using PnP.Scanning.Core.ClassicPages.Collection.Page;
using PnP.Scanning.Core.ClassicPages.Collection.File;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages;
#nullable enable
using System.Collections.Concurrent;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Audit;
using PnP.Scanning.Core.ClassicPages.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Contracts.Site;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Supplies tenant-neutral native file and item observations to production collection with replay counters and controlled failures.
/// </summary>
internal sealed class ClassicContentFixture : IClassicContentReader
{
    internal const string Site = "https://contoso.sharepoint.com/sites/a";
    internal const string App = "22222222-2222-2222-2222-222222222222";
    internal static readonly Guid SiteCollectionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid WebId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid ListId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    internal static readonly Guid PostsListId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid RetiredFileId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    internal readonly ConcurrentDictionary<string, int> Calls = new();
    internal string? BlockAt;
    internal TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string Revision = "original";
    internal bool FailEnrichment;
    internal bool DeniedDiscovery;
    internal bool IncludeBlog;
    internal bool IncludeRetiredFile;
    internal bool PostsOnNonBlogWeb;
    internal bool IncludeSecondBlog;
    internal bool IncludePhysicalFacts = true;
    internal Guid? ExtraFileAssessment;
    internal Func<WebFileInventory, WebFileInventory>? TransformInventory;
    internal Func<ListItemBatch, ListItemBatch>? TransformBatch;
    internal Func<WebListItemQueries, WebListItemQueries>? TransformQueries;
    private Guid assessment;
    private bool otherComponents;
    internal ClassicCollectionOptions Options = new(false, true, false, false, 14);

    /// <summary>
    /// Binds synthetic acquisition to the actual pipeline assessment identity and fixed typed options.
    /// </summary>
    internal Task<IClassicContentReader> OpenAsync(CollectionContext context, CancellationToken token)
    {
        assessment = context.AssessmentId;
        otherComponents = ClassicPageModule.HasOtherComponents(context.Options);
        Options = ClassicPageModule.Options(context.Options);
        Count("open");
        return Task.FromResult<IClassicContentReader>(this);
    }

    /// <summary>
    /// Counts original requests so tests can prove replay does not reacquire committed source.
    /// </summary>
    private void Count(string operation) => Calls.AddOrUpdate(operation, 1, (_, count) => count + 1);

    /// <summary>
    /// Creates an internal test request without changing the existing public start surface.
    /// </summary>
    internal static CollectRequest Request(bool usage = false, bool homeOnly = false) =>
        new()
        {
            Module = ClassicPageModule.Key,
            ParametersJson = VersionedJson.Empty.Json,
            CollectionOptions = MakeOptions(usage, homeOnly)
        };

    /// <summary>
    /// Creates the same Classic Pages options used by the original CLI with synthetic authentication metadata.
    /// </summary>
    internal static StartRequest MakeOptions(bool usage = false, bool homeOnly = false)
    {
        StartRequest request = new()
        {
            Mode = Mode.Classic.ToString(),
            Threads = 2,
            Environment = "Production",
            AuthMode = "Device",
            ApplicationId = App,
            TenantId = SiteCollectionId.ToString(),
            Tenant = "contoso.sharepoint.com",
            SitesList = Site,
        };
        Core.Scanners.ClassicStartRequestBuilder.AddClassicProperties(
            request,
            [ClassicComponent.Pages],
            false,
            !usage,
            false,
            homeOnly,
            14);
        return request;
    }

    /// <summary>
    /// Returns fixed site-selection evidence and audit timestamps, independent of collection wall-clock time.
    /// </summary>
    public Task<ClassicCollectionScope> ReadSiteSelectionAsync(
        ClassicCollectionOptions options,
        CancellationToken token)
    {
        Count("sites");
        DateTime end = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        DiscoveryEvidence evidence = new()
        {
            ScanId = assessment,
            RecordKey = "assessment:site-selection",
            RowType = DiscoveryRowType.Scope,
            ScopeType = "SiteSelection",
            Url = Site,
            ObservationMethod = "DeclaredSites",
            DiscoveryStatus = "Complete",
            ExpectedChildCount = 1,
            ObservedChildCount = 1,
            ObservedAtUtc = end,
        };
        return Task.FromResult(
            new ClassicCollectionScope([Site], [], true, options, end.AddDays(-14), end, [evidence], otherComponents));
    }

    /// <summary>
    /// Returns typed Web scopes with captured string templates rather than positional arrays.
    /// </summary>
    public Task<SiteCollectionScope> ReadWebScopesAsync(string siteUrl, CancellationToken token)
    {
        Count("webs");
        return Task.FromResult(
            new SiteCollectionScope(
                Site,
                [new WebScope(Site, "/", IncludeBlog && !PostsOnNonBlogWeb ? "BLOG#0" : "STS#0")],
                SourceReadState.Complete));
    }

    /// <summary>
    /// Builds deterministic native Web facts for recorded source tests.
    /// </summary>
    internal WebFacts Web(string webTemplate = "STS#0") =>
        new(
            Site,
            "/",
            webTemplate,
            SiteCollectionId,
            WebId,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            [],
            [],
            "SitePages/Home.aspx",
            SourceReadState.Complete,
            true,
            SourceReadState.Complete,
            null,
            1033,
            null,
            SourceReadState.NotAttempted,
            SourceReadState.Complete);

    /// <summary>
    /// Returns minimum native identity without optional Web enrichment.
    /// </summary>
    public Task<WebFacts> ReadIdentityAsync(WebScope scope, CancellationToken token)
    {
        Count("identity");
        return Task.FromResult(Web(scope.WebTemplate));
    }

    /// <summary>
    /// Returns recorded optional metadata or fails independently of the already acquired physical inventory.
    /// </summary>
    public Task<WebFacts> ReadWebFactsAsync(WebScope scope, CancellationToken token)
    {
        Count("metadata");
        if (FailEnrichment) throw new InvalidOperationException("Feature-dependent metadata denied");
        return Task.FromResult(Web(scope.WebTemplate));
    }

    /// <summary>
    /// Describes native ASPX candidates, including a real file without a backing item and a denied metadata read.
    /// </summary>
    internal DiscoveryEvidence[] DiscoveryRows() =>
        Enumerable.Range(1, 7)
            .Select(number => new DiscoveryEvidence
            {
                ScanId = assessment,
                SiteUrl = Site,
                WebUrl = "/",
                RecordKey = "page:" + number,
                RowType = DiscoveryRowType.Page,
                ScopeType = "File",
                Url = number == 1 ? "/sites/a/SitePages/Home.aspx" : $"/sites/a/SitePages/{number}.aspx",
                SiteCollectionId = SiteCollectionId,
                WebId = WebId,
                ListId = number == 6 ? null : ListId,
                ListItemId = number == 6 ? null : number,
                FileUniqueId = Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
                FileName = number == 1 ? "Home.aspx" : number + ".aspx",
                DiscoveryStatus = "Discovered",
                ObservationMethod = "RecordedREST",
                ObservedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            })
            .ToArray();

    /// <summary>
    /// Journals independent physical discovery pages and returns original object facts, including pointpub in the same library as ASPX.
    /// </summary>
    public async Task<WebFileInventory> ReadFileInventoryAsync(
        WebFacts web,
        AcquisitionJournal journal,
        CancellationToken token)
    {
        Count("discovery");
        if (DeniedDiscovery)
            return new(
                Site,
                "/",
                [
                    new DiscoveryEvidence
                    {
                        ScanId = assessment,
                        SiteUrl = Site,
                        WebUrl = "/",
                        RecordKey = "scope:web",
                        RowType = DiscoveryRowType.Scope,
                        ScopeType = "Web",
                        DiscoveryStatus = "Denied",
                        ErrorCodes = "http_403",
                        ErrorDetail = "Synthetic access denied",
                    }
                ],
                []);
        await journal.ReadOrAcquireAsync(
            "DiscoveryRequest",
            "batch:0",
            () => Task.FromResult(new { nextLink = "batch:1", Revision }),
            token);
        if (BlockAt == "discovery")
        {
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }

        await journal.ReadOrAcquireAsync(
            "DiscoveryRequest",
            "batch:1",
            () => Task.FromResult(new { nextLink = (string?)null, Revision }),
            token);
        DiscoveryEvidence[] candidates = DiscoveryRows();
        DiscoveryEvidence scope = new()
        {
            ScanId = assessment,
            SiteUrl = Site,
            WebUrl = "/",
            RecordKey = "scope:web",
            RowType = DiscoveryRowType.Scope,
            ScopeType = "Web",
            DiscoveryStatus = "Complete",
            Url = Site,
            ExpectedChildCount = 7,
            ObservedChildCount = 7,
            ObservedAtUtc = web.LastItemUserModifiedDate,
        };
        List<PhysicalFileFacts> files = IncludePhysicalFacts
            ? candidates.Select(candidate => new PhysicalFileFacts
                {
                    ScanId = assessment,
                    SiteCollectionId = SiteCollectionId,
                    WebId = WebId,
                    FileUniqueId = candidate.FileUniqueId!.Value,
                    SiteUrl = Site,
                    WebUrl = "/",
                    ServerRelativeUrl = candidate.Url,
                    FileName = candidate.FileName,
                    ListId = candidate.ListId,
                    ListItemId = candidate.ListItemId,
                    Length = 123,
                    FileReadState = "Complete",
                    ItemMetadataReadState = "NotRequested",
                    NoExecuteReadState = "NotRequested",
                    CoverageState = "Observed",
                    FirstObservedAtUtc = candidate.ObservedAtUtc,
                    ObservedAtUtc = candidate.ObservedAtUtc,
                    ProvenanceJson = "[{\"source\":\"RecordedFolder\"}]",
                })
                .ToList()
            : [];
        files.Add(
            new PhysicalFileFacts
            {
                ScanId = ExtraFileAssessment ?? assessment,
                SiteCollectionId = SiteCollectionId,
                WebId = WebId,
                FileUniqueId = Guid.Parse("00000000-0000-0000-0000-000000000008"),
                SiteUrl = Site,
                WebUrl = "/",
                ServerRelativeUrl = "/sites/a/data.txt",
                FileName = "data.txt",
                Length = 456,
                FileReadState = "Complete",
                ItemMetadataReadState = "NotRequested",
                NoExecuteReadState = "NotRequested",
                CoverageState = "Observed",
                FirstObservedAtUtc = scope.ObservedAtUtc,
                ObservedAtUtc = scope.ObservedAtUtc,
            });
        if (IncludeRetiredFile)
            files.Add(
                new PhysicalFileFacts
                {
                    ScanId = assessment,
                    SiteCollectionId = SiteCollectionId,
                    WebId = WebId,
                    ListId = ListId,
                    ListItemId = 200,
                    FileUniqueId = RetiredFileId,
                    SiteUrl = Site,
                    WebUrl = "/",
                    ServerRelativeUrl = "/sites/a/SitePages/post.pointpub",
                    FileName = "post.pointpub",
                    Length = 123,
                    CoverageState = "Observed",
                    FileReadState = "Complete",
                    ItemMetadataReadState = "NotRequested",
                    NoExecuteReadState = "NotRequested",
                    FirstObservedAtUtc = scope.ObservedAtUtc,
                    ObservedAtUtc = scope.ObservedAtUtc,
                });
        WebFileInventory inventory = new(Site, "/", candidates.Append(scope).ToArray(), files.ToArray());
        return TransformInventory?.Invoke(inventory) ?? inventory;
    }

    /// <summary>
    /// Returns fixed file enrichment while retaining explicit denied metadata and file-only evidence.
    /// </summary>
    public async Task<FilePageEvidence> ReadFileEnrichmentAsync(
        WebFacts web,
        DiscoveryEvidence candidate,
        bool skipUsers,
        CancellationToken token)
    {
        Count(candidate.RecordKey);
        if (BlockAt == candidate.RecordKey)
        {
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }

        if (candidate.ListItemId == null)
            return FilePageSourceReader.UnavailableFileEnrichment(web, candidate, SourceReadState.NotAttempted) with
            {
                FileLevelOnly = true, ListRootFolderUrls = []
            };
        if (candidate.ListItemId == 7)
            return FilePageSourceReader.UnavailableFileEnrichment(
                web,
                candidate,
                new("Denied", "Recorded page metadata 403"));
        Dictionary<string, SourceField> fields = new()
        {
            ["ID"] = SourceField.Of("Int32", candidate.ListItemId),
            ["FileRef"] = SourceField.Of("String", candidate.Url),
            ["FileLeafRef"] = SourceField.Of("String", candidate.FileName),
            ["Title"] = SourceField.Of("String", Revision + " " + candidate.FileName),
            ["Modified"] = SourceField.Of("DateTime", web.LastItemUserModifiedDate),
            ["File_x0020_Type"] = SourceField.Of("String", "aspx"),
            ["BSN"] = SourceField.Of("String", "1"),
            ["NoExecute"] = SourceField.Of("String", candidate.ListItemId == 3 ? " 1 " : "1"),
        };
        if (!skipUsers)
            fields["Editor"] = SourceField.Of(
                "User",
                new { Email = "author@contoso.com", LookupValue = "Author", LookupId = 9 });
        if (candidate.ListItemId == 1) fields["WikiField"] = SourceField.Of("String", "<div>Hello recorded wiki</div>");
        if (candidate.ListItemId == 2)
            fields["HTML_x0020_File_x0020_Type"] = SourceField.Of("String", "SharePoint.WebPartPage.Document");
        if (candidate.ListItemId == 3)
        {
            fields["ContentTypeId"] = SourceField.Of("String", "0x01010007FF3E057FA8AB4AA42FCB67B453FFC1");
            fields["PublishingPageLayout"] = SourceField.Of(
                "Url",
                new { Url = "/_catalogs/masterpage/ArticleLeft.aspx", Description = "ArticleLeft" });
        }

        if (candidate.ListItemId == 4)
            fields["ClientSideApplicationId"] = SourceField.Of(
                "Guid",
                Guid.Parse("B6917CB1-93A0-4B97-A84D-7CF49975D4EC"));
        PhysicalFileFacts facts = PhysicalFileProjection.FromItemMetadata(
            candidate,
            fields.ToDictionary(field => field.Key, field => field.Value.ToValue()));
        return new(
            Site,
            "/",
            candidate.RecordKey,
            candidate.ListId,
            "Site Pages",
            "/sites/a/SitePages",
            fields,
            new() { ["vti_setuppath"] = SourceField.Of("String", "site templates/sts/default.aspx") },
            candidate.ListItemId is 2 or 3 ? [Part("Main", 0), Part("TitleBar", 1)] : [],
            SourceReadState.Complete,
            candidate.ListItemId is 1 or 2 or 3 ? SourceReadState.Complete : SourceReadState.NotAttempted,
            null,
            SourceReadState.NotAttempted,
            facts,
            PublishingLayout: candidate.ListItemId == 3
                ? new("/_catalogs/masterpage/ArticleLeft.aspx", "Customized", "Customized", SourceReadState.Complete)
                : null);
    }

    /// <summary>
    /// Creates a recorded original web-part export for detached readiness rules.
    /// </summary>
    internal static ClassicPageWebPartSource Part(string zone, int index) =>
        new(
            Guid.Parse($"00000000-0000-0000-1111-{index:D12}"),
            null,
            zone,
            index,
            "Content",
            false,
            false,
            "All",
            new() { ["Content"] = SourceField.Of("String", "Recorded web part content") },
            "<webParts><webPart xmlns=\"http://schemas.microsoft.com/WebPart/v3\"><metaData><type name=\"Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c\" /></metaData></webPart></webParts>",
            SourceReadState.Complete);

    /// <summary>
    /// Returns query kinds and observed list templates without assigning content PageType during acquisition.
    /// </summary>
    public Task<WebListItemQueries> ReadListItemQueriesAsync(WebFacts web, CancellationToken token)
    {
        Count("blog-lists");
        List<ListItemQueryScope> queries = [];
        if (IncludeBlog)
            queries.Add(
                new(
                    PostsListId,
                    "Posts",
                    "/sites/a/Lists/Posts",
                    ListItemQueryKind.Posts,
                    301,
                    "/sites/a/Lists/Posts/Post.aspx"));
        if (IncludeBlog && IncludeSecondBlog)
            queries.Add(
                new(
                    Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    "More Posts",
                    "/sites/a/Lists/Posts2",
                    ListItemQueryKind.Posts,
                    301,
                    "/sites/a/Lists/Posts2/Post.aspx"));
        WebListItemQueries scope = new(Site, "/", queries.ToArray(), SourceReadState.Complete);
        return Task.FromResult(TransformQueries?.Invoke(scope) ?? scope);
    }

    /// <summary>
    /// Returns independent Posts item identities and their original paging receipts without any File association reads.
    /// </summary>
    public Task<ListItemBatch> ReadListItemBatchAsync(
        WebFacts web,
        ListItemQueryScope query,
        string? paging,
        bool skipUsers,
        CancellationToken token)
    {
        Count("blog:" + (paging ?? "first"));
        int itemId = paging == null ? 100 : 101;
        Dictionary<string, SourceField> fields = new()
        {
            ["ID"] = SourceField.Of("Int32", itemId),
            ["Title"] = SourceField.Of("String", "Recorded blog post"),
            ["Modified"] = SourceField.Of("DateTime", web.LastItemUserModifiedDate),
        };
        ListItemFacts item = new(itemId, fields);
        ListItemBatch batch = new(
            Site,
            "/",
            query.ListId,
            query.ListTitle,
            query.ListUrl,
            [item],
            paging == null ? "second" : null,
            SourceReadState.Complete,
            query.QueryKind,
            query.ListTemplate,
            query.DisplayFormUrl,
            ContentTypeForms: query.ContentTypeForms);
        return Task.FromResult(TransformBatch?.Invoke(batch) ?? batch);
    }

    /// <summary>
    /// Records a fixed original audit body and its settled query window.
    /// </summary>
    public async Task CollectAuditAsync(
        ClassicCollectionScope scope,
        AcquisitionJournal journal,
        CancellationToken token)
    {
        Count("audit");
        await journal.ReadOrAcquireAsync(
            "AuditPage",
            "0|0",
            () => Task.FromResult(
                new ClassicPageAuditPageSource(
                    0,
                    "recorded-query",
                    0,
                    "{\"value\":[{\"operation\":\"ClassicPageViewed\",\"objectId\":\"https://contoso.sharepoint.com/sites/a/SitePages/Home.aspx\",\"userId\":\"one\"},{\"operation\":\"ClassicPageEdited\",\"objectId\":\"https://contoso.sharepoint.com/sites/a/SitePages/Home.aspx\",\"userId\":\"ONE\"}]}",
                    null)),
            token);
        await journal.ReadOrAcquireAsync(
            "AuditChunk",
            "0",
            () => Task.FromResult(
                new ClassicPageAuditChunkSource(
                    0,
                    scope.AuditWindowStart,
                    scope.AuditWindowEnd,
                    "recorded-query",
                    "succeeded",
                    null)),
            token);
    }

    /// <summary>
    /// Captures recorded mixed-component results under their actual assessment and Web scope.
    /// </summary>
    public Task<ClassicComponentSource> ReadClassicComponentsAsync(WebFacts web, CancellationToken token)
    {
        Count("components");
        return Task.FromResult(
            new ClassicComponentSource(
                Site,
                "/",
                SourceReadState.Complete,
                new Dictionary<string, JsonElement[]>
                {
                    ["ClassicList"] =
                    [
                        JsonSerializer.SerializeToElement(
                            new Core.Storage.ClassicList
                            {
                                ScanId = assessment,
                                SiteUrl = Site,
                                WebUrl = "/",
                                ListId = ListId,
                                ListUrl = "/sites/a/Lists/Tasks",
                                ListTitle = "Tasks",
                                RemediationCode = "CL4",
                            })
                    ],
                    ["ClassicWebSummary"] =
                    [
                        JsonSerializer.SerializeToElement(
                            new Core.Storage.ClassicWebSummary
                            {
                                ScanId = assessment,
                                SiteUrl = Site,
                                WebUrl = "/",
                                ClassicLists = 1,
                                AggregatedRemediationCodes = "CL4",
                            })
                    ],
                }));
    }

    /// <summary>
    /// Completes recorded source disposal without external resources.
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}