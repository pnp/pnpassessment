using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using System.Linq.Expressions;
using System.Net;
using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.WebParts;
using PnP.Core.Auth;
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Collection.Audit;
using PnP.Scanning.Core.Pipeline.Collection.List;
using PnP.Scanning.Core.Pipeline.Collection.Page;
using PnP.Scanning.Core.Pipeline.Collection.File;
using PnP.Scanning.Core.Pipeline.Collection.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Site;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using AuthenticationManager = PnP.Scanning.Core.Authentication.AuthenticationManager;
using SdkList = PnP.Core.Model.SharePoint.IList;


namespace PnP.Scanning.Core.Pipeline.Collection.Module;

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
    private CsomLease Csom(PnPContext pnp, CancellationToken token)
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
            evidence.Select(CollectionJson.Convert<DiscoveryEvidence>).ToArray(),
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
                result.ScopeEvidence.Select(CollectionJson.Convert<DiscoveryEvidence>).ToArray());
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
        using CsomLease? csom = Csom(pnp, token);
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

    /// <summary>
    /// Acquires journaled physical-file and ASPX discovery evidence under the selected native Web scope.
    /// </summary>
    public async Task<WebFileInventory> ReadFileInventoryAsync(
        WebFacts web,
        AcquisitionJournal journal,
        CancellationToken token)
    {
        Dictionary<string, Storage.ClassicPageDiscovery>? rows =
            new Dictionary<string, Storage.ClassicPageDiscovery>(StringComparer.Ordinal);
        Dictionary<(Guid Site, Guid File), SPFile>? files = new Dictionary<(Guid Site, Guid File), SPFile>();
        AssessmentDiscoveryWriter? writer = new AssessmentDiscoveryWriter(
            (batch, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                foreach (Storage.ClassicPageDiscovery? row in batch)
                {
                    if (rows.TryGetValue(row.RecordKey, out Storage.ClassicPageDiscovery? previous))
                        AssessmentDiscoveryWriter.Reconcile(previous, row);
                    rows[row.RecordKey] = row;
                }

                return Task.CompletedTask;
            },
            (batch, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                foreach (SPFile? file in batch)
                {
                    if (file.SiteCollectionId != web.SiteCollectionId || file.WebId.HasValue && file.WebId != web.WebId)
                        throw new SnapshotIntegrityException(
                            "A physical file observation belongs to a foreign Web scope.");
                    if (file.SiteUrl != web.SiteUrl || file.WebUrl != web.WebUrl)
                    {
                        System.Text.Json.JsonElement[]? evidence =
                            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(
                                file.ProvenanceJson) ?? throw new SnapshotIntegrityException(
                                "Physical file provenance must be an evidence array.");
                        file.ProvenanceJson = System.Text.Json.JsonSerializer.Serialize(
                            evidence.Cast<object>()
                                .Append(
                                    new
                                    {
                                        source = "SnapshotWebScope",
                                        rawSiteUrl = file.SiteUrl,
                                        rawWebUrl = file.WebUrl
                                    }));
                        file.SiteUrl = web.SiteUrl;
                        file.WebUrl = web.WebUrl;
                    }

                    (Guid SiteCollectionId, Guid FileUniqueId) key = (file.SiteCollectionId, file.FileUniqueId);
                    files[key] = SPFileWriter.Consolidate(files.GetValueOrDefault(key), file);
                    layoutReader.Seed(web.SiteCollectionId, file.ServerRelativeUrl, file.CustomizedPageStatus);
                }

                return Task.CompletedTask;
            });
        Uri? uri = new Uri(web.SiteUrl.TrimEnd('/') + (web.WebUrl == "/" ? "" : web.WebUrl));
        using ClassicPageDiscoveryJournal? factory = new ClassicPageDiscoveryJournal(
            new PnPContextSharePointAspxRestClientFactory(services.ContextFactory, auth),
            journal);
        using SharePointLiveAspxDiscoveryProvider? provider = new SharePointLiveAspxDiscoveryProvider(
            new(
                "assessment:" + context.AssessmentId,
                "scheduled-web",
                "classic-assessment",
                DiscoveryHash.Of(
                    "classic-assessment",
                    context.AssessmentId.ToString("D"),
                    web.SiteCollectionId.ToString("D"),
                    web.WebId.ToString("D")),
                Intent: ClassicPageModule.Options(context.Options).HomePageOnly
                    ? AspxDiscoveryIntent.HomePageOnly
                    : AspxDiscoveryIntent.FullInventory),
            factory,
            new(web.SiteCollectionId, new Uri(web.SiteUrl), web.WebId, uri, uri.AbsolutePath, web.WebTemplate));
        await new AssessmentWebDiscovery(context.AssessmentId, web.SiteUrl, web.WebUrl, writer).RunAsync(
            provider,
            token);
        try
        {
            using PnPContext? pnp = await services.ContextFactory.CreateAsync(
                uri,
                auth,
                token,
                new PnPContextOptions
                {
                    Properties = new Dictionary<string, object> { ["PipelineAssessmentId"] = context.AssessmentId }
                });
            using CsomLease? csom = Csom(pnp, token);
            AspxPageEnricher? enricher = new AspxPageEnricher(pnp, csom.Context, new Uri(web.SiteUrl), uri);
            if (!ClassicPageModule.Options(context.Options).HomePageOnly) await enricher.LoadRolesAsync(token);
            foreach (SPFile? file in files.Values.Where(f => SharePointAspxUrlPolicy.IsAspxPath(f.ServerRelativeUrl)))
                file.AspxEvidenceJson = await journal.ReadOrAcquireAsync(
                    "AspxSource",
                    file.SiteCollectionId.ToString("D") + "|" + file.FileUniqueId.ToString("D"),
                    () => enricher.ReadAsync(CollectionJson.Convert<PhysicalFileFacts>(file), token),
                    token,
                    json => System.Text.Json.JsonSerializer.Deserialize<AspxPageEvidence>(json)!.SourceReadState ==
                            "Available"
                        ? AcquisitionStatus.Complete
                        : AcquisitionStatus.Partial);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            Serilog.Log.Warning(error, "ASPX enrichment context failed for {WebUrl}; retaining discovered files", uri);
            foreach (SPFile? file in
                     files.Values.Where(f =>
                         SharePointAspxUrlPolicy.IsAspxPath(f.ServerRelativeUrl) && f.AspxEvidenceJson == null))
                file.AspxEvidenceJson = System.Text.Json.JsonSerializer.Serialize(
                    new AspxPageEvidence(
                        "Failed",
                        null,
                        null,
                        "Unresolved",
                        null,
                        AssessmentWebDiscovery.ErrorDetail(error),
                        null,
                        null,
                        null,
                        null,
                        "Failed",
                        DateTime.UtcNow));
        }

        token.ThrowIfCancellationRequested();
        return new(
            web.SiteUrl,
            web.WebUrl,
            rows.Values.OrderBy(x => x.RecordKey, StringComparer.Ordinal)
                .Select(CollectionJson.Convert<DiscoveryEvidence>)
                .ToArray(),
            files.Values.OrderBy(x => x.SiteCollectionId)
                .ThenBy(x => x.FileUniqueId)
                .Select(CollectionJson.Convert<PhysicalFileFacts>)
                .ToArray());
    }

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
        using CsomLease? csom = Csom(pnp, token);
        FilePageEvidence? input = await ReadFileEnrichmentInputsAsync(pnp, csom.Context, web, page, skipUsers, token);
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
    /// Captures physical page inputs without performing detached readiness analysis.
    /// </summary>
    internal static async Task<FilePageEvidence> ReadFileEnrichmentInputsAsync(
        PnPContext pnp,
        ClientContext csom,
        WebFacts web,
        DiscoveryEvidence page,
        bool skipUsers,
        CancellationToken token)
    {
        SdkList? list = pnp.Web.Lists.AsRequested().FirstOrDefault(x => x.Id == page.ListId);
        if (list == null || page.ListItemId == null)
        {
            string[]? roots = pnp.Web.Lists.AsRequested().Select(x => x.RootFolder.ServerRelativeUrl).ToArray();
            FilePageEvidence? input = UnavailableFileEnrichment(web, page, SourceReadState.NotAttempted) with
            {
                FileLevelOnly = true, ListRootFolderUrls = roots, OwningListMatched = list != null
            };
            if (FileLevelPageProjection.FileLevelPageType(SetupPathClassifier.Classify(page.SetupPath)) == null)
                return input;
            try
            {
                (Dictionary<string, SourceField>? fileProperties, ClassicPageWebPartSource[]? fileParts) =
                    await PageWebPartSourceReader.ReadAsync(
                        csom,
                        page.Url,
                        ClassicPageRules.WebPartPage,
                        input.Fields,
                        token);
                return input with
                {
                    FileProperties = fileProperties, WebParts = fileParts, WebPartsState = SourceReadState.Complete
                };
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                return input with { WebPartsState = Failure(error) };
            }
        }

        FilePageEvidence? metadata = await ReadFileItemMetadataAsync(list, page, skipUsers, token);
        if (!metadata.MetadataState.Succeeded) return metadata;
        Dictionary<string, SourceField>? fields = metadata.Fields;
        string? type =
            SharePointLiveAspxDiscoveryProvider.IsPublishingPageContentType(
                fields.GetValueOrDefault("ContentTypeId")?.Text ?? page.ContentTypeId)
                ? ClassicPageRules.PublishingPage
                : ClassicPageRules.GetPageType(fields.ToDictionary(x => x.Key, x => x.Value.ToValue()));
        Dictionary<string, SourceField>? properties = new Dictionary<string, SourceField>();
        ClassicPageWebPartSource[] parts = [];
        SourceReadState? partsState = SourceReadState.NotAttempted;
        if (type is ClassicPageRules.WikiPage or ClassicPageRules.WebPartPage or ClassicPageRules.PublishingPage)
        {
            try
            {
                (properties, parts) = await PageWebPartSourceReader.ReadAsync(csom, page.Url, type, fields, token);
                partsState = SourceReadState.Complete;
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                partsState = Failure(error);
            }
        }

        string? display = null;
        SourceReadState? fallback = SourceReadState.NotAttempted;
        if (!web.CanModernizeState.Succeeded && HomePageDetector.IsHomePage(page.Url, web.WelcomePage) == true)
        {
            try
            {
                Microsoft.SharePoint.Client.ListItem? item = csom.Web.GetFileByServerRelativeUrl(page.Url)
                    .ListItemAllFields;
                csom.Load(item.ContentType, ct => ct.DisplayFormTemplateName);
                await csom.ExecuteQueryAsync();
                display = item.ContentType.DisplayFormTemplateName;
                fallback = SourceReadState.Complete;
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                fallback = Failure(error);
            }
        }

        token.ThrowIfCancellationRequested();
        return metadata with
        {
            FileProperties = properties,
            WebParts = parts,
            WebPartsState = partsState,
            ContentTypeDisplayFormTemplateName = display,
            HomeFallbackState = fallback
        };
    }

    /// <summary>
    /// Reads the exact discovered list item and retains returned identity and explicit metadata failure.
    /// </summary>
    internal static async Task<FilePageEvidence> ReadFileItemMetadataAsync(
        SdkList list,
        DiscoveryEvidence page,
        bool skipUsers,
        CancellationToken token)
    {
        if (page.ListId != list.Id || page.ListItemId == null || page.ListItemId <= 0)
            throw new InvalidDataException(
                "Physical page metadata requires its discovered list and list-item identity.");

        ListItemFieldBatch batch = await LoadItemFieldsAsync(
            list,
            ListItemQueryBuilder.Create(
                new() { "WikiField", "HTML_x0020_File_x0020_Type", "ClientSideApplicationId", "NoExecute" },
                false,
                page.ListItemId.Value,
                skipUsers),
            null,
            token);
        SourceReadState? state = SourceReadState.Complete;
        Dictionary<string, SourceField>? fields = new Dictionary<string, SourceField>();
        if (batch.Items.Length != 1 || batch.NextPage != null ||
            !batch.Items[0].TryGetValue("ID", out SourceField? id) || id.Text != page.ListItemId.Value.ToString())
            state = new("NotReturned", "The discovered list item was not returned uniquely.");
        else
        {
            fields = batch.Items[0];
            ClassicPageRules.ResolvePhysicalPageUrl(fields.ToDictionary(x => x.Key, x => x.Value.ToValue()), page.Url);
        }

        Storage.ClassicPageDiscovery? physical = CollectionJson.Convert<Storage.ClassicPageDiscovery>(page);
        SPFile? fileFacts = state.Succeeded
            ? SPFileFactProjection.FromItemMetadata(physical, fields.ToDictionary(x => x.Key, x => x.Value.ToValue()))
            : SPFileFactProjection.FromItemFailure(physical, new InvalidDataException(state.Error ?? state.Status));
        return new(
            page.SiteUrl,
            page.WebUrl,
            page.RecordKey,
            batch.ListId,
            batch.ListTitle,
            batch.ListUrl,
            fields,
            new(),
            [],
            state,
            SourceReadState.NotAttempted,
            null,
            SourceReadState.NotAttempted,
            fileFacts == null ? null : CollectionJson.Convert<PhysicalFileFacts>(fileFacts),
            ReturnedItems: batch.Items);
    }

    /// <summary>
    /// Constructs an unavailable page input without inventing successful metadata or web-part acquisition.
    /// </summary>
    internal static FilePageEvidence UnavailableFileEnrichment(
        WebFacts web,
        DiscoveryEvidence page,
        SourceReadState state)
    {
        SPFile? facts = state.Status is "Failed" or "Denied" or "NotReturned"
            ? SPFileFactProjection.FromItemFailure(
                CollectionJson.Convert<Storage.ClassicPageDiscovery>(page),
                new InvalidDataException(state.Error ?? state.Status))
            : null;
        return new(
            web.SiteUrl,
            web.WebUrl,
            page.RecordKey,
            page.ListId,
            null,
            null,
            new(),
            new(),
            [],
            state,
            SourceReadState.NotAttempted,
            null,
            SourceReadState.NotAttempted,
            facts == null ? null : CollectionJson.Convert<PhysicalFileFacts>(facts));
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
        ListItemBatch batch = await ListItemContentReader.ReadBatchAsync(pnp, web, list, paging, skipUsers, token);
        if (list.QueryKind != ListItemQueryKind.PointPublishingFiles || !batch.State.Succeeded ||
            batch.Items.Length == 0)
            return batch;
        using CsomLease csom = Csom(pnp, token);
        IReadOnlyDictionary<int, FileItemAssociation> associations = await NativeFileAssociationReader.ReadAsync(
            csom.Context,
            context.AssessmentId,
            web,
            list.ListId,
            batch.Items,
            token);
        return batch with
        {
            Items = batch.Items.Select(item => item with { FileAssociation = associations[item.ListItemId] })
                .ToArray(),
        };
    }

    /// <summary>
    /// Runs retained non-page Classic collectors and captures their owned result rows as source evidence.
    /// </summary>
    public async Task<ClassicComponentSource> ReadClassicComponentsAsync(WebFacts web, CancellationToken token)
    {
        ClassicOptions? options = (ClassicOptions)OptionsBase.FromScannerInput(context.Options);
        options.Pages = false;
        using PnPContext? pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        using CsomLease? csom = Csom(pnp, token);
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
        await Storage.Pipeline.ClassicComponentStorage.ResetAsync(context.AssessmentId, web, token);
        await ClassicScanner.ExecuteOtherComponentsAsync(scanner, pnp, csom.Context, token);
        return await Storage.Pipeline.ClassicComponentStorage.CaptureAsync(context.AssessmentId, web, token);
    }

    /// <summary>
    /// Loads a bounded native list-stream page and detaches its returned typed items and continuation.
    /// </summary>
    internal static async Task<ListItemFieldBatch> LoadItemFieldsAsync(
        SdkList list,
        string query,
        string? paging,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        list.Items.Clear();
        Dictionary<string, object>? response = await list.LoadListDataAsStreamAsync(
            new PnP.Core.Model.SharePoint.RenderListDataOptions
            {
                ViewXml = query, RenderOptions = RenderListDataOptionsFlags.ListData, Paging = paging
            });
        token.ThrowIfCancellationRequested();
        return new(
            list.Id,
            list.Title,
            list.RootFolder.ServerRelativeUrl,
            list.Items.AsRequested().Select(ItemFields).ToArray(),
            response.TryGetValue("NextHref", out object? next) ? next?.ToString()?.TrimStart('?') : null,
            SourceReadState.Complete);
    }

    /// <summary>
    /// Converts a captured property bag into detached typed source fields.
    /// </summary>
    internal static Dictionary<string, SourceField> Fields(IDictionary<string, object> fields) =>
        fields.ToDictionary(x => x.Key, x => Field(x.Value), StringComparer.Ordinal);

    /// <summary>
    /// Captures returned fields and validates the modeled native item ID.
    /// </summary>
    internal static Dictionary<string, SourceField> ItemFields(IListItem item)
    {
        Dictionary<string, SourceField>? fields = Fields(item.Values);
        // RenderListDataAsStream puts ID in the modeled Id property and deliberately omits it
        // from Values. Persist that returned identity along with the detached field values.
        if (fields.TryGetValue("ID", out SourceField? existing) && existing.ToValue() is int fieldId &&
            fieldId != item.Id)
            throw new InvalidDataException("The list item's modeled ID differs from its returned ID field.");
        fields["ID"] = SourceField.Of("Int32", item.Id);
        return fields;
    }

    /// <summary>
    /// Preserves a supported captured field type and rejects unsupported SDK value kinds.
    /// </summary>
    private static SourceField Field(object? value) =>
        value switch
        {
            null => SourceField.Of("Null", null),
            IFieldUserValue user => SourceField.Of("User", new { user.Email, user.LookupValue, user.LookupId }),
            IFieldLookupValue lookup => SourceField.Of("Lookup", new { lookup.LookupValue, lookup.LookupId }),
            Microsoft.SharePoint.Client.FieldUserValue user => SourceField.Of(
                "User",
                new { Email = user.Email, user.LookupValue, user.LookupId }),
            Microsoft.SharePoint.Client.FieldUrlValue url => SourceField.Of("Url", new { url.Url, url.Description }),
            IFieldUrlValue url => SourceField.Of("Url", new { url.Url, url.Description }),
            DateTime date => SourceField.Of("DateTime", date),
            Guid guid => SourceField.Of("Guid", guid),
            int number => SourceField.Of("Int32", number),
            long number => SourceField.Of("Int64", number),
            double number => SourceField.Of("Double", number),
            decimal number => SourceField.Of("Decimal", number),
            bool boolean => SourceField.Of("Boolean", boolean),
            string text => SourceField.Of("String", text),
            System.Text.Json.JsonElement json => SourceField.Of("Json", json),
            _ => throw new NotSupportedException($"Unsupported source field type '{value.GetType().FullName}'."),
        };

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