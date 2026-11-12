using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Collection.Discovery;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using System.Linq.Expressions;
using Microsoft.SharePoint.Client;
using PnP.Core.Auth;
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Collection.Audit;
using PnP.Scanning.Core.ClassicPages.Collection.List;
using PnP.Scanning.Core.ClassicPages.Collection.Page;
using PnP.Scanning.Core.ClassicPages.Collection.File;
using PnP.Scanning.Core.Pipeline.Collection.Shared;
using PnP.Scanning.Core.ClassicPages.Contracts.List;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Site;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using PnP.Scanning.Core.Services;


namespace PnP.Scanning.Core.ClassicPages.Collection.Module;

/// <summary>
/// Defines authenticated acquisition operations returning detached source evidence.
/// </summary>
internal interface IClassicContentReader : IAsyncDisposable
{
    /// <summary>
    /// Returns fixed site selection, audit window, and original enumeration evidence.
    /// </summary>
    Task<ClassicCollectionScope> ReadSiteSelectionAsync(ClassicCollectionOptions options, CancellationToken token);

    /// <summary>
    /// Returns captured Web identities and templates together with original enumeration and policy evidence.
    /// </summary>
    Task<SiteCollectionScope> ReadWebScopesAsync(string siteUrl, CancellationToken token);

    /// <summary>
    /// Reads native site and Web identity without requiring optional enrichment metadata.
    /// </summary>
    Task<WebFacts> ReadIdentityAsync(WebScope scope, CancellationToken token);

    /// <summary>
    /// Reads optional Web metadata and home-page inputs into detached acquisition facts.
    /// </summary>
    Task<WebFacts> ReadWebFactsAsync(WebScope scope, CancellationToken token);

    /// <summary>
    /// Acquires journaled physical-file and ASPX discovery evidence under the selected native Web scope.
    /// </summary>
    Task<WebFileInventory> ReadFileInventoryAsync(WebFacts web, AcquisitionJournal journal, CancellationToken token);

    /// <summary>
    /// Acquires one discovered page's typed fields and optional web-part and layout evidence.
    /// </summary>
    Task<FilePageEvidence> ReadFileEnrichmentAsync(
        WebFacts web,
        DiscoveryEvidence page,
        bool skipUsers,
        CancellationToken token);

    /// <summary>
    /// Returns captured candidate content-list scope for one Web.
    /// </summary>
    Task<WebListItemQueries> ReadListItemQueriesAsync(WebFacts web, CancellationToken token);

    /// <summary>
    /// Returns one captured native content-item page and continuation for the selected list.
    /// </summary>
    Task<ListItemBatch> ReadListItemBatchAsync(
        WebFacts web,
        ListItemQueryScope list,
        string? paging,
        bool skipUsers,
        CancellationToken token);

    /// <summary>
    /// Runs retained non-page Classic collectors and captures their owned result rows as source evidence.
    /// </summary>
    Task<ClassicComponentSource> ReadClassicComponentsAsync(WebFacts web, CancellationToken token);

    /// <summary>
    /// Collects audit receipts for the fixed scope using collection-only authentication.
    /// </summary>
    Task CollectAuditAsync(ClassicCollectionScope scope, AcquisitionJournal journal, CancellationToken token);
}

/// <summary>
/// Owns authenticated SDK and CSOM reads and converts responses into detached evidence.
/// </summary>
internal sealed class ClassicContentReader(CollectionContext context, CollectionServices services)
    : IClassicContentReader
{
    private readonly PublishingLayoutReader layoutReader = new();

    private readonly ExternalAuthenticationProvider auth = new((_, scopes) =>
        services.Authentication.GetAccessTokenAsync(scopes));

    private PnPContextOptions ContextOptions =>
        new()
        {
            Properties = new Dictionary<string, object> { ["PipelineAssessmentId"] = context.AssessmentId },
            AdditionalSitePropertiesOnCreate =
                new Expression<Func<ISite, object>>[] { s => s.Features.QueryProperties(f => f.DefinitionId) },
            AdditionalWebPropertiesOnCreate = new Expression<Func<IWeb, object>>[]
            {
                w => w.LastItemUserModifiedDate, w => w.Features.QueryProperties(f => f.DefinitionId),
                w => w.Lists.QueryProperties(
                    l => l.Id,
                    l => l.Title,
                    l => l.TemplateType,
                    l => l.BaseType,
                    l => l.RootFolder.QueryProperties(f => f.ServerRelativeUrl)),
            },
        };

    /// <summary>
    /// Creates an authenticated SDK context for the selected site and Web with collection cancellation.
    /// </summary>
    private Task<PnPContext> OpenAsync(string site, string web, CancellationToken token) =>
        services.ContextFactory.CreateAsync(
            new Uri(site.TrimEnd('/') + (web == "/" ? "" : web)),
            auth,
            token,
            ContextOptions);

    /// <summary>
    /// Creates an owned CSOM context and linked transport without owning the shared SDK client.
    /// </summary>
    internal static CsomLease Csom(PnPContext pnp, CollectionServices services, CancellationToken token)
    {
        ClientContext? client = new ClientContext(pnp.Uri) { DisableReturnValueCache = true };
        client.ExecutingWebRequest += (_, args) =>
        {
            token.ThrowIfCancellationRequested();
            args.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + services.Authentication
                .GetAccessTokenAsync(new[] { pnp.Uri.GetLeftPart(UriPartial.Authority) + "/.default" })
                .GetAwaiter()
                .GetResult();
        };
        HttpClient? transport =
            new HttpClient(new CollectionHttpClient(pnp.RestClient.Client, token))
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        client.WebRequestExecutorFactory = new HttpClientWebRequestExecutorFactory(transport);
        return new CsomLease(client, transport);
    }

    /// <summary>
    /// Owns one CSOM context and its transport without owning the shared SDK HTTP client.
    /// </summary>
    internal sealed class CsomLease(ClientContext context, HttpClient transport) : IDisposable
    {
        internal ClientContext Context { get; } = context;

        /// <summary>
        /// Disposes the owned CSOM context and transport without disposing the shared SDK client.
        /// </summary>
        public void Dispose()
        {
            try
            {
                Context.Dispose();
            }
            finally
            {
                transport.Dispose();
            }
        }
    }

    /// <summary>
    /// Retains an exception as an explicit denied or failed source read outcome.
    /// </summary>
    internal static SourceReadState Failure(Exception error) =>
        new(
            AssessmentWebDiscovery.Classify(error) == DiscoveryTerminalOutcome.Denied ? "Denied" : "Failed",
            error.GetType().Name + ": " + error.GetBaseException().Message);

    /// <summary>
    /// Returns fixed site selection, audit window, and original enumeration evidence.
    /// </summary>
    public async Task<ClassicCollectionScope> ReadSiteSelectionAsync(
        ClassicCollectionOptions options,
        CancellationToken token)
    {
        StartRequest? request = context.Options.Clone();
        request.Mode = Mode.Classic.ToString();
        request.Properties.Clear();
        ClassicStartRequestBuilder.AddClassicProperties(
            request,
            new[] { ClassicComponent.Pages },
            options.ExportWebPartProperties,
            options.SkipUsageInformation,
            options.SkipUserInformation,
            options.HomePageOnly,
            options.AuditLogWindowDays,
            options.ExcludeSiteTemplates);
        List<Storage.ClassicPageDiscovery>? evidence = new List<Storage.ClassicPageDiscovery>();
        List<string>? sites = await services.SiteEnumeration.EnumerateSiteCollectionsToScanAsync(
            request,
            services.Authentication,
            message => Serilog.Log.Information("{Message}", message),
            evidence,
            token);
        token.ThrowIfCancellationRequested();
        foreach (Storage.ClassicPageDiscovery? row in evidence) row.ScanId = context.AssessmentId;
        DateTime end = DateTime.UtcNow;
        return new(
            sites.ToArray(),
            [],
            !string.IsNullOrWhiteSpace(request.SitesList) || !string.IsNullOrWhiteSpace(request.SitesFile),
            options,
            end.AddDays(-options.AuditLogWindowDays),
            end,
            evidence.Select(ClassicPages.Compatibility.DiscoveryEvidenceMapper.FromLegacy).ToArray(),
            ClassicPageModule.HasOtherComponents(context.Options));
    }

    /// <summary>
    /// Returns captured Web identities and templates together with original enumeration and policy evidence.
    /// </summary>
    public async Task<SiteCollectionScope> ReadWebScopesAsync(string siteUrl, CancellationToken token)
    {
        try
        {
            WebEnumerationResult? result = await services.SiteEnumeration.EnumerateWebsToScanAsync(
                context.AssessmentId,
                siteUrl,
                new ClassicOptions
                {
                    Pages = true,
                    ExcludeSiteTemplates = ClassicPageModule.Options(context.Options).ExcludeSiteTemplates
                },
                services.Authentication,
                isRestart: false,
                cancellationToken: token,
                contextPropertyKey: "PipelineAssessmentId");
            token.ThrowIfCancellationRequested();
            return new(
                siteUrl,
                result.Webs.Select(web => new WebScope(siteUrl, web.WebUrl, web.WebTemplate)).ToArray(),
                SourceReadState.Complete,
                result.ScopeEvidence.Select(ClassicPages.Compatibility.DiscoveryEvidenceMapper.FromLegacy).ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return new(siteUrl, [], Failure(error));
        }
    }

    /// <summary>
    /// Reads optional Web metadata and home-page inputs into detached acquisition facts.
    /// </summary>
    public async Task<WebFacts> ReadWebFactsAsync(WebScope scope, CancellationToken token)
    {
        using PnPContext? pnp = await OpenAsync(scope.SiteCollectionUrl, scope.WebUrl, token);
        await pnp.Site.GetAsync(s => s.Id);
        await pnp.Web.GetAsync(w => w.Id);
        using CsomLease? csom = Csom(pnp, services, token);
        return await ReadWebFactsAsync(
            pnp,
            csom.Context,
            scope.SiteCollectionUrl,
            scope.WebUrl,
            scope.WebTemplate,
            token);
    }

    /// <summary>
    /// Reads native site and Web identity without requiring optional enrichment metadata.
    /// </summary>
    public async Task<WebFacts> ReadIdentityAsync(WebScope scope, CancellationToken token)
    {
        using PnPContext? pnp = await services.ContextFactory.CreateAsync(
            new Uri(scope.SiteCollectionUrl.TrimEnd('/') + (scope.WebUrl == "/" ? "" : scope.WebUrl)),
            auth,
            token,
            new PnPContextOptions
            {
                Properties = new Dictionary<string, object> { ["PipelineAssessmentId"] = context.AssessmentId }
            });
        await pnp.Site.GetAsync(s => s.Id);
        await pnp.Web.GetAsync(w => w.Id, w => w.Url, w => w.ServerRelativeUrl);
        token.ThrowIfCancellationRequested();
        return new(
            scope.SiteCollectionUrl,
            scope.WebUrl,
            scope.WebTemplate,
            pnp.Site.Id,
            pnp.Web.Id,
            default,
            [],
            [],
            null,
            SourceReadState.NotAttempted,
            null,
            SourceReadState.NotAttempted,
            null,
            0,
            null,
            SourceReadState.NotAttempted,
            SourceReadState.Complete);
    }

    /// <summary>
    /// Captures feature and home-page inputs with independent read outcomes.
    /// </summary>
    internal static async Task<WebFacts> ReadWebFactsAsync(
        PnPContext pnp,
        ClientContext csom,
        string siteUrl,
        string webUrl,
        string webTemplate,
        CancellationToken token)
    {
        string? welcome = null, master = null, localized = null;
        int language = 0;
        bool? canModernize = null;
        SourceReadState? welcomeState = SourceReadState.NotAttempted;
        SourceReadState? primaryState = SourceReadState.NotAttempted;
        SourceReadState? fallbackState = SourceReadState.NotAttempted;
        try
        {
            csom.ClientTag = "SPDev:M365Scanner";
            csom.Load(csom.Web.RootFolder, f => f.WelcomePage);
            await csom.ExecuteQueryAsync();
            welcome = csom.Web.RootFolder.WelcomePage ?? "";
            welcomeState = SourceReadState.Complete;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            welcomeState = Failure(ex);
        }

        try
        {
            ModernizeHomepageResult? value = csom.Web.CanModernizeHomepage;
            csom.Load(value);
            await csom.ExecuteQueryAsync();
            canModernize = value.CanModernizeHomepage;
            primaryState = SourceReadState.Complete;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            primaryState = Failure(ex);
        }

        if (!primaryState.Succeeded)
        {
            try
            {
                csom.Load(csom.Web, w => w.MasterUrl, w => w.Language);
                await csom.ExecuteQueryAsync();
                master = csom.Web.MasterUrl;
                language = (int)csom.Web.Language;
                ClientResult<string>? resource = Microsoft.SharePoint.Client.Utilities.Utility.GetLocalizedString(
                    csom,
                    "$Resources:WikiPageHomePageName",
                    "core",
                    language);
                await csom.ExecuteQueryAsync();
                localized = resource.Value;
                fallbackState = SourceReadState.Complete;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                fallbackState = Failure(ex);
            }
        }

        token.ThrowIfCancellationRequested();
        return new(
            siteUrl,
            webUrl,
            webTemplate,
            pnp.Site.Id,
            pnp.Web.Id,
            pnp.Web.LastItemUserModifiedDate,
            pnp.Site.Features.AsRequested().Select(x => x.DefinitionId).ToArray(),
            pnp.Web.Features.AsRequested().Select(x => x.DefinitionId).ToArray(),
            welcome,
            welcomeState,
            canModernize,
            primaryState,
            master,
            language,
            localized,
            fallbackState,
            SourceReadState.Complete);
    }

    /// <summary>Acquires journaled physical-file and ASPX evidence in the selected native Web scope.</summary>
    public Task<WebFileInventory> ReadFileInventoryAsync(
        WebFacts web,
        AcquisitionJournal journal,
        CancellationToken token) =>
        new FileInventoryReader(context, services, auth, layoutReader).ReadAsync(web, journal, token);

    /// <summary>
    /// Acquires one discovered page's typed fields and optional web-part and layout evidence.
    /// </summary>
    public async Task<FilePageEvidence> ReadFileEnrichmentAsync(
        WebFacts web,
        DiscoveryEvidence page,
        bool skipUsers,
        CancellationToken token)
    {
        using PnPContext? pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        using CsomLease? csom = Csom(pnp, services, token);
        FilePageEvidence? input = await FilePageSourceReader.ReadFileEnrichmentInputsAsync(
            pnp,
            csom.Context,
            web,
            page,
            skipUsers,
            token);
        string? layoutUrl = SnapshotPageLayout.GetPublishingPageLayoutUrl(input.Fields);
        if (!input.MetadataState.Succeeded || layoutUrl == null) return input;
        ClassicPublishingLayoutSource? layout = await layoutReader.ReadAsync(
            web.SiteCollectionId,
            new Uri(web.SiteUrl),
            layoutUrl,
            async path =>
            {
                Microsoft.SharePoint.Client.File? file = csom.Context.Site.RootWeb.GetFileByServerRelativeUrl(path);
                csom.Context.Load(file, value => value.CustomizedPageStatus);
                await csom.Context.ExecuteQueryAsync();
                return file.CustomizedPageStatus.ToString();
            },
            token);
        return input with { PublishingLayout = layout };
    }

    /// <summary>
    /// Returns captured candidate content-list scope for one Web.
    /// </summary>
    public async Task<WebListItemQueries> ReadListItemQueriesAsync(WebFacts web, CancellationToken token)
    {
        using PnPContext? pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        return await ListItemContentReader.ReadListsAsync(pnp, web, token);
    }

    /// <summary>
    /// Returns one captured native content-item page and continuation for the selected list.
    /// </summary>
    public async Task<ListItemBatch> ReadListItemBatchAsync(
        WebFacts web,
        ListItemQueryScope list,
        string? paging,
        bool skipUsers,
        CancellationToken token)
    {
        using PnPContext? pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        return await ListItemContentReader.ReadBatchAsync(pnp, web, list, paging, skipUsers, token);
    }

    /// <summary>
    /// Runs retained non-page Classic collectors and captures their owned result rows as source evidence.
    /// </summary>
    public async Task<ClassicComponentSource> ReadClassicComponentsAsync(WebFacts web, CancellationToken token)
    {
        ClassicOptions? options = (ClassicOptions)OptionsBase.FromScannerInput(context.Options);
        options.Pages = false;
        using PnPContext? pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        using CsomLease? csom = Csom(pnp, services, token);
        await ClassicAssessmentMetadata.LoadAsync(pnp, ClassicScanner.AssessmentContextOptions(options), token);
        ClassicScanner? scanner = new ClassicScanner(
            null,
            services.SiteEnumeration.StorageManager,
            services.ContextFactory,
            context.AssessmentId,
            web.SiteUrl,
            web.WebUrl,
            web.WebTemplate,
            options);
        await ClassicPages.Compatibility.ClassicComponentStorage.ResetAsync(context.AssessmentId, web, token);
        await ClassicScanner.ExecuteOtherComponentsAsync(scanner, pnp, csom.Context, token);
        return await ClassicPages.Compatibility.ClassicComponentStorage.CaptureAsync(context.AssessmentId, web, token);
    }

    /// <summary>
    /// Collects audit receipts for the fixed scope using collection-only authentication.
    /// </summary>
    public Task CollectAuditAsync(ClassicCollectionScope scope, AcquisitionJournal journal, CancellationToken token) =>
        new ClassicPageAuditCollector(context.Options, services.Authentication).CollectAsync(scope, journal, token);

    /// <summary>
    /// Completes source disposal after each owned network context has settled.
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}