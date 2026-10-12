#nullable enable
using System.Collections.Concurrent;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Audit;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Site;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Supplies fixed online source responses to the production Classic Pages collector, with request counters and interruption controls.
/// </summary>
internal sealed class ClassicPageFixture : IClassicPageOnlineSource
{
    internal const string Site = "https://contoso.sharepoint.com/sites/a";
    internal const string App = "22222222-2222-2222-2222-222222222222";
    internal static readonly Guid SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid WebId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid ListId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    internal readonly ConcurrentDictionary<string, int> Calls = new();
    internal string? BlockAt;
    internal TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string Revision = "original";
    internal bool FailEnrichment;
    internal bool IncludeBlog;
    private Guid assessment;
    internal ClassicPageSourceOptions Options = new(false, true, false, false, 14);
    /// <summary>
    /// Binds the requested assessment and collection options and returns the recorded online source.
    /// </summary>
    internal Task<IClassicPageOnlineSource> OpenAsync(CollectionContext context, CancellationToken token)
    {
        assessment = context.AssessmentId;
        Options = ClassicPageModule.Options(context.Options);
        Count("open");
        return Task.FromResult<IClassicPageOnlineSource>(this);
    }
    /// <summary>
    /// Counts calls by acquisition operation so replay tests can detect repeated online work.
    /// </summary>
    private void Count(string key) => Calls.AddOrUpdate(key, 1, (_, n) => n + 1);
    /// <summary>
    /// Builds a production Classic Pages collect request with the selected audit and home-page options.
    /// </summary>
    internal static CollectRequest Request(bool usage = false, bool homeOnly = false) => new()
    {
        Module = "classicpage", ParametersJson = VersionedJson.Empty.Json,
        CollectionOptions = MakeOptions(usage, homeOnly),
    };
    /// <summary>
    /// Creates fixed tenant and authentication options and attaches the selected Classic page properties.
    /// </summary>
    internal static StartRequest MakeOptions(bool usage = false, bool homeOnly = false)
    {
        var request = new StartRequest { Mode = Mode.ClassicPage.ToString(), Threads = 2, Environment = "Production", AuthMode = "Device",
            ApplicationId = App, TenantId = SiteId.ToString(), Tenant = "contoso.sharepoint.com", SitesList = Site };
        global::PnP.Scanning.Core.Scanners.ClassicStartRequestBuilder.AddClassicProperties(request, new[] { ClassicComponent.Pages }, false, !usage, false, homeOnly, 14);
        return request;
    }
    /// <summary>
    /// Returns the fixed site scope and audit interval with deterministic selection evidence.
    /// </summary>
    public Task<ClassicPageScopeSource> ReadSitesAsync(ClassicPageSourceOptions options, CancellationToken token)
    {
        Count("sites");
        var end = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var evidence = new ClassicPageDiscoveryRow { ScanId = assessment, RecordKey = "assessment:site-selection", RowType = "Scope", ScopeType = "SiteSelection",
            Url = Site, ObservationMethod = "DeclaredSites", DiscoveryStatus = "Complete", ExpectedChildCount = 1, ObservedChildCount = 1, ObservedAtUtc = end };
        return Task.FromResult(new ClassicPageScopeSource([Site], [], true, options, end.AddDays(-14), end, [evidence]));
    }
    /// <summary>
    /// Returns the fixed root web and selected site template with successful enumeration state.
    /// </summary>
    public Task<ClassicPageSiteScope> ReadWebsAsync(string siteUrl, CancellationToken token)
    {
        Count("webs");
        return Task.FromResult(new ClassicPageSiteScope(Site, ["/"], [IncludeBlog ? "BLOG#0" : "STS#0"], SourceReadState.Complete));
    }
    /// <summary>
    /// Builds detached web identity and successful home-page facts for the selected test template.
    /// </summary>
    internal ClassicPageWebSource Web(string template = "STS#0") => new(Site, "/", template, SiteId, WebId,
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), [], [], "SitePages/Home.aspx", SourceReadState.Complete,
        true, SourceReadState.Complete, null, 1033, null, SourceReadState.NotAttempted, SourceReadState.Complete);
    /// <summary>
    /// Returns the fixed site and web identity while counting minimum-identity reads.
    /// </summary>
    public Task<ClassicPageWebSource> ReadIdentityAsync(string siteUrl, string webUrl, string template, CancellationToken token)
    { Count("identity"); return Task.FromResult(Web(template)); }
    /// <summary>
    /// Returns fixed web enrichment or injects the configured optional metadata failure.
    /// </summary>
    public Task<ClassicPageWebSource> ReadWebAsync(string siteUrl, string webUrl, string template, CancellationToken token)
    {
        Count("metadata");
        if (FailEnrichment) throw new InvalidOperationException("Feature-dependent metadata denied");
        return Task.FromResult(Web(template));
    }
    /// <summary>
    /// Builds deterministic physical page discovery records, including a page without list-item identity.
    /// </summary>
    internal ClassicPageDiscoveryRow[] DiscoveryRows() => Enumerable.Range(1, 7).Select(i => new ClassicPageDiscoveryRow
    {
        ScanId = assessment, SiteUrl = Site, WebUrl = "/", RecordKey = "page:" + i, RowType = "Page", ScopeType = "Folder",
        Url = i == 1 ? "/sites/a/SitePages/Home.aspx" : "/sites/a/SitePages/" + i + ".aspx",
        SiteCollectionId = SiteId, WebId = WebId, ListId = i == 6 ? null : ListId, ListItemId = i == 6 ? null : i,
        FileUniqueId = Guid.Parse("00000000-0000-0000-0000-" + i.ToString("D12")), FileName = i == 1 ? "Home.aspx" : i + ".aspx",
        DiscoveryStatus = "Discovered", ObservationMethod = "RecordedREST", ObservedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    }).ToArray();
    /// <summary>
    /// Journals two recorded discovery pages and optionally interrupts between them to exercise durable replay.
    /// </summary>
    public async Task<ClassicPageDiscoverySource> DiscoverAsync(ClassicPageWebSource web, ClassicPageAcquisitionJournal journal, CancellationToken token)
    {
        Count("discovery");
        // Model two independently committed discovery pages and a cancellation after the first.
        await journal.ReadOrAcquireAsync("DiscoveryRequest", "batch:0", () => Task.FromResult(new { nextLink = "batch:1", Revision }), token);
        if (BlockAt == "discovery") { Reached.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        await journal.ReadOrAcquireAsync("DiscoveryRequest", "batch:1", () => Task.FromResult(new { nextLink = (string?)null, Revision }), token);
        var rows = DiscoveryRows();
        var scope = new ClassicPageDiscoveryRow { ScanId = assessment, SiteUrl = Site, WebUrl = "/", RecordKey = "scope:web", RowType = "Scope", ScopeType = "Web",
            DiscoveryStatus = "Complete", Url = Site, ExpectedChildCount = 7, ObservedChildCount = 7, ObservedAtUtc = web.LastItemUserModifiedDate };
        return new(Site, "/", rows.Append(scope).ToArray());
    }
    /// <summary>
    /// Returns recorded page fields and web part evidence, with optional page acquisition interruption.
    /// </summary>
    public async Task<ClassicPageItemSource> ReadPageAsync(ClassicPageWebSource web, ClassicPageDiscoveryRow page, bool skipUsers, CancellationToken token)
    {
        Count(page.RecordKey);
        if (BlockAt == page.RecordKey) { Reached.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
        if (page.ListItemId == null) return ClassicPageOnlineSource.EmptyPage(web, page, SourceReadState.NotAttempted);
        if (page.ListItemId == 7) return ClassicPageOnlineSource.EmptyPage(web, page, new("Denied", "Recorded page metadata 403"));
        var fields = new Dictionary<string, SourceField>
        {
            ["ID"] = SourceField.Of("Int32", page.ListItemId), ["FileRef"] = SourceField.Of("String", page.Url),
            ["FileLeafRef"] = SourceField.Of("String", page.FileName), ["Title"] = SourceField.Of("String", Revision + " " + page.FileName),
            ["Modified"] = SourceField.Of("DateTime", web.LastItemUserModifiedDate), ["File_x0020_Type"] = SourceField.Of("String", "aspx"),
            ["BSN"] = SourceField.Of("String", "1"),
        };
        if (!skipUsers) fields["Editor"] = SourceField.Of("User", new { Email = "author@contoso.com", LookupValue = "Author", LookupId = 9 });
        if (page.ListItemId == 1) fields["WikiField"] = SourceField.Of("String", "<div>Hello recorded wiki</div>");
        if (page.ListItemId == 2) fields["HTML_x0020_File_x0020_Type"] = SourceField.Of("String", "SharePoint.WebPartPage.Document");
        if (page.ListItemId == 3)
        {
            fields["ContentTypeId"] = SourceField.Of("String", "0x01010007FF3E057FA8AB4AA42FCB67B453FFC1");
            fields["PublishingPageLayout"] = SourceField.Of("Url", new { Url = "/_catalogs/masterpage/ArticleLeft.aspx", Description = "ArticleLeft" });
        }
        if (page.ListItemId == 4) fields["ClientSideApplicationId"] = SourceField.Of("Guid", Guid.Parse("B6917CB1-93A0-4B97-A84D-7CF49975D4EC"));
        var properties = new Dictionary<string, SourceField> { ["vti_setuppath"] = SourceField.Of("String", "site templates/sts/default.aspx") };
        var parts = page.ListItemId is 2 or 3 ? new[] { Part("Main", 0), Part("TitleBar", 1) } : Array.Empty<ClassicPageWebPartSource>();
        return new(Site, "/", page.RecordKey, ListId, "Site Pages", "/sites/a/SitePages", fields, properties, parts,
            SourceReadState.Complete, page.ListItemId is 1 or 2 or 3 ? SourceReadState.Complete : SourceReadState.NotAttempted,
            null, SourceReadState.NotAttempted);
    }
    /// <summary>
    /// Builds a recorded web part definition and export XML for offline inventory analysis.
    /// </summary>
    internal static ClassicPageWebPartSource Part(string zone, int index) => new(Guid.Parse("00000000-0000-0000-1111-" + index.ToString("D12")), null, zone, index,
        "Content", false, false, "All", new() { ["Content"] = SourceField.Of("String", "Recorded web part content") },
        "<webParts><webPart xmlns=\"http://schemas.microsoft.com/WebPart/v3\"><metaData><type name=\"Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c\" /></metaData></webPart></webParts>", SourceReadState.Complete);
    /// <summary>
    /// Returns the optional recorded blog batch and continuation for blog collection tests.
    /// </summary>
    public Task<ClassicPageBlogBatchSource?> ReadBlogBatchAsync(ClassicPageWebSource web, string? paging, bool skipUsers, CancellationToken token)
    {
        Count("blog:" + (paging ?? "first"));
        var fields = new Dictionary<string, SourceField> { ["ID"] = SourceField.Of("Int32", paging == null ? 100 : 101),
            ["Title"] = SourceField.Of("String", "Recorded blog post"), ["Modified"] = SourceField.Of("DateTime", web.LastItemUserModifiedDate) };
        return Task.FromResult<ClassicPageBlogBatchSource?>(new(Site, "/", ListId, "Posts", "/sites/a/Lists/Posts", [fields], paging == null ? "second" : null, SourceReadState.Complete));
    }
    /// <summary>
    /// Commits recorded audit chunk and records-page evidence for production audit finalization.
    /// </summary>
    public async Task CollectAuditAsync(ClassicPageScopeSource scope, ClassicPageAcquisitionJournal journal, CancellationToken token)
    {
        Count("audit");
        await journal.ReadOrAcquireAsync("AuditPage", "0|0", () => Task.FromResult(new ClassicPageAuditPageSource(0, "recorded-query", 0,
            "{\"value\":[{\"operation\":\"ClassicPageViewed\",\"objectId\":\"https://contoso.sharepoint.com/sites/a/SitePages/Home.aspx\",\"userId\":\"one\"},{\"operation\":\"ClassicPageEdited\",\"objectId\":\"https://contoso.sharepoint.com/sites/a/SitePages/Home.aspx\",\"userId\":\"ONE\"}]}", null)), token);
        await journal.ReadOrAcquireAsync("AuditChunk", "0", () => Task.FromResult(new ClassicPageAuditChunkSource(0, scope.AuditWindowStart, scope.AuditWindowEnd, "recorded-query", "succeeded", null)), token);
    }
    /// <summary>
    /// Completes fixture disposal because its recorded source owns no live network resources.
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
