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
using ClassicPageDiscovery = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageDiscoveryRow;

namespace PnP.Scanning.Core.Pipeline.Collection.Module;

internal interface IClassicPageOnlineSource : IAsyncDisposable
{
    Task<ClassicPageScopeSource> ReadSitesAsync(ClassicPageSourceOptions options, CancellationToken token);
    Task<ClassicPageSiteScope> ReadWebsAsync(string siteUrl, CancellationToken token);
    Task<ClassicPageWebSource> ReadIdentityAsync(string siteUrl, string webUrl, string template, CancellationToken token);
    Task<ClassicPageWebSource> ReadWebAsync(string siteUrl, string webUrl, string template, CancellationToken token);
    Task<ClassicPageDiscoverySource> DiscoverAsync(ClassicPageWebSource web, ClassicPageAcquisitionJournal journal, CancellationToken token);
    Task<ClassicPageItemSource> ReadPageAsync(ClassicPageWebSource web, ClassicPageDiscovery page, bool skipUsers, CancellationToken token);
    Task<ClassicBlogListScopeSource> ReadBlogListsAsync(ClassicPageWebSource web, CancellationToken token);
    Task<ClassicPageBlogBatchSource> ReadBlogBatchAsync(ClassicPageWebSource web, ClassicBlogListSource list,
        string? paging, bool skipUsers, CancellationToken token);
    Task<ClassicComponentSource> ReadClassicComponentsAsync(ClassicPageWebSource web, CancellationToken token);
    Task CollectAuditAsync(ClassicPageScopeSource scope, ClassicPageAcquisitionJournal journal, CancellationToken token);
}

internal sealed class ClassicPageOnlineSource(CollectionContext context, CollectionServices services) : IClassicPageOnlineSource
{
    private readonly PublishingLayoutSourceReader layoutReader = new();
    private readonly ExternalAuthenticationProvider auth = new((_, scopes) => services.Authentication.GetAccessTokenAsync(scopes));
    private PnPContextOptions ContextOptions => new()
    {
        Properties = new Dictionary<string, object> { ["PipelineAssessmentId"] = context.AssessmentId },
        AdditionalSitePropertiesOnCreate = new Expression<Func<ISite, object>>[] { s => s.Features.QueryProperties(f => f.DefinitionId) },
        AdditionalWebPropertiesOnCreate = new Expression<Func<IWeb, object>>[]
        {
            w => w.LastItemUserModifiedDate, w => w.Features.QueryProperties(f => f.DefinitionId),
            w => w.Lists.QueryProperties(l => l.Id, l => l.Title, l => l.TemplateType, l => l.BaseType,
                l => l.RootFolder.QueryProperties(f => f.ServerRelativeUrl)),
        },
    };
    private Task<PnPContext> OpenAsync(string site, string web, CancellationToken token) =>
        services.ContextFactory.CreateAsync(new Uri(site.TrimEnd('/') + (web == "/" ? "" : web)), auth, token, ContextOptions);
    private CsomLease Csom(PnPContext pnp, CancellationToken token)
    {
        var client = new ClientContext(pnp.Uri) { DisableReturnValueCache = true };
        client.ExecutingWebRequest += (_, args) =>
        {
            token.ThrowIfCancellationRequested();
            args.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + services.Authentication
                .GetAccessTokenAsync(new[] { pnp.Uri.GetLeftPart(UriPartial.Authority) + "/.default" }).GetAwaiter().GetResult();
        };
        var transport = new HttpClient(new CollectionHttpClient(pnp.RestClient.Client, token)) { Timeout = Timeout.InfiniteTimeSpan };
        client.WebRequestExecutorFactory = new HttpClientWebRequestExecutorFactory(transport);
        return new CsomLease(client, transport);
    }

    internal sealed class CsomLease(ClientContext context, HttpClient transport) : IDisposable
    {
        internal ClientContext Context { get; } = context;
        public void Dispose()
        {
            try { Context.Dispose(); }
            finally { transport.Dispose(); }
        }
    }
    internal static SourceReadState Failure(Exception error) => new(
        AssessmentWebDiscovery.Classify(error) == DiscoveryTerminalOutcome.Denied ? "Denied" : "Failed", error.GetType().Name + ": " + error.GetBaseException().Message);

    public async Task<ClassicPageScopeSource> ReadSitesAsync(ClassicPageSourceOptions options, CancellationToken token)
    {
        var request = context.Options.Clone();
        request.Mode = Mode.Classic.ToString();
        request.Properties.Clear();
        ClassicStartRequestBuilder.AddClassicProperties(request, new[] { ClassicComponent.Pages }, options.ExportWebPartProperties,
            options.SkipUsageInformation, options.SkipUserInformation, options.HomePageOnly, options.AuditLogWindowDays,
            options.ExcludeSiteTemplates);
        var evidence = new List<Storage.ClassicPageDiscovery>();
        var sites = await services.SiteEnumeration.EnumerateSiteCollectionsToScanAsync(request, services.Authentication,
            message => Serilog.Log.Information("{Message}", message), evidence, token);
        token.ThrowIfCancellationRequested();
        foreach (var row in evidence) row.ScanId = context.AssessmentId;
        var end = DateTime.UtcNow;
        return new(sites.ToArray(), [], !string.IsNullOrWhiteSpace(request.SitesList) || !string.IsNullOrWhiteSpace(request.SitesFile),
            options, end.AddDays(-options.AuditLogWindowDays), end, evidence.Select(ClassicPageSourceJson.Convert<ClassicPageDiscovery>).ToArray(),
            ClassicPageModule.HasOtherComponents(context.Options));
    }

    public async Task<ClassicPageSiteScope> ReadWebsAsync(string siteUrl, CancellationToken token)
    {
        try
        {
            var result = await services.SiteEnumeration.EnumerateWebsToScanAsync(context.AssessmentId, siteUrl,
                new ClassicOptions { Pages = true, ExcludeSiteTemplates = ClassicPageModule.Options(context.Options).ExcludeSiteTemplates },
                services.Authentication, isRestart: false, cancellationToken: token, contextPropertyKey: "PipelineAssessmentId");
            token.ThrowIfCancellationRequested();
            return new(siteUrl, result.Webs.Select(x => x.WebUrl).ToArray(), result.Webs.Select(x => x.WebTemplate).ToArray(), SourceReadState.Complete,
                result.ScopeEvidence.Select(ClassicPageSourceJson.Convert<ClassicPageDiscovery>).ToArray());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { return new(siteUrl, [], [], Failure(error)); }
    }

    public async Task<ClassicPageWebSource> ReadWebAsync(string siteUrl, string webUrl, string template, CancellationToken token)
    {
        using var pnp = await OpenAsync(siteUrl, webUrl, token);
        await pnp.Site.GetAsync(s => s.Id);
        await pnp.Web.GetAsync(w => w.Id);
        using var csom = Csom(pnp, token);
        return await ReadWebFactsAsync(pnp, csom.Context, siteUrl, webUrl, template, token);
    }

    public async Task<ClassicPageWebSource> ReadIdentityAsync(string siteUrl, string webUrl, string template, CancellationToken token)
    {
        using var pnp = await services.ContextFactory.CreateAsync(new Uri(siteUrl.TrimEnd('/') + (webUrl == "/" ? "" : webUrl)), auth, token,
            new PnPContextOptions { Properties = new Dictionary<string, object> { ["PipelineAssessmentId"] = context.AssessmentId } });
        await pnp.Site.GetAsync(s => s.Id);
        await pnp.Web.GetAsync(w => w.Id, w => w.Url, w => w.ServerRelativeUrl);
        token.ThrowIfCancellationRequested();
        return new(siteUrl, webUrl, template, pnp.Site.Id, pnp.Web.Id, default, [], [], null,
            SourceReadState.NotAttempted, null, SourceReadState.NotAttempted, null, 0, null, SourceReadState.NotAttempted, SourceReadState.Complete);
    }

    internal static async Task<ClassicPageWebSource> ReadWebFactsAsync(PnPContext pnp, ClientContext csom, string siteUrl, string webUrl, string template, CancellationToken token)
    {
        string? welcome = null, master = null, localized = null;
        int language = 0;
        bool? canModernize = null;
        var welcomeState = SourceReadState.NotAttempted;
        var primaryState = SourceReadState.NotAttempted;
        var fallbackState = SourceReadState.NotAttempted;
        try
        {
            csom.ClientTag = "SPDev:M365Scanner";
            csom.Load(csom.Web.RootFolder, f => f.WelcomePage);
            await csom.ExecuteQueryAsync();
            welcome = csom.Web.RootFolder.WelcomePage ?? "";
            welcomeState = SourceReadState.Complete;
        }
        catch (Exception ex) when (!token.IsCancellationRequested) { welcomeState = Failure(ex); }
        try
        {
            var value = csom.Web.CanModernizeHomepage;
            csom.Load(value);
            await csom.ExecuteQueryAsync();
            canModernize = value.CanModernizeHomepage;
            primaryState = SourceReadState.Complete;
        }
        catch (Exception ex) when (!token.IsCancellationRequested) { primaryState = Failure(ex); }
        if (!primaryState.Succeeded)
        {
            try
            {
                csom.Load(csom.Web, w => w.MasterUrl, w => w.Language);
                await csom.ExecuteQueryAsync();
                master = csom.Web.MasterUrl; language = (int)csom.Web.Language;
                var resource = Microsoft.SharePoint.Client.Utilities.Utility.GetLocalizedString(csom, "$Resources:WikiPageHomePageName", "core", language);
                await csom.ExecuteQueryAsync();
                localized = resource.Value;
                fallbackState = SourceReadState.Complete;
            }
            catch (Exception ex) when (!token.IsCancellationRequested) { fallbackState = Failure(ex); }
        }
        token.ThrowIfCancellationRequested();
        return new(siteUrl, webUrl, template, pnp.Site.Id, pnp.Web.Id, pnp.Web.LastItemUserModifiedDate,
            pnp.Site.Features.AsRequested().Select(x => x.DefinitionId).ToArray(), pnp.Web.Features.AsRequested().Select(x => x.DefinitionId).ToArray(),
            welcome, welcomeState, canModernize, primaryState, master, language, localized, fallbackState, SourceReadState.Complete);
    }

    public async Task<ClassicPageDiscoverySource> DiscoverAsync(ClassicPageWebSource web, ClassicPageAcquisitionJournal journal, CancellationToken token)
    {
        var rows = new Dictionary<string, Storage.ClassicPageDiscovery>(StringComparer.Ordinal);
        var files = new Dictionary<(Guid Site, Guid File), SPFile>();
        var writer = new AssessmentDiscoveryWriter((batch, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            foreach (var row in batch)
            {
                if (rows.TryGetValue(row.RecordKey, out var previous)) AssessmentDiscoveryWriter.Reconcile(previous, row);
                rows[row.RecordKey] = row;
            }
            return Task.CompletedTask;
        }, (batch, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in batch)
            {
                if (file.SiteCollectionId != web.SiteId || file.WebId.HasValue && file.WebId != web.WebId)
                    throw new SnapshotIntegrityException("A physical file observation belongs to a foreign Web scope.");
                if (file.SiteUrl != web.SiteUrl || file.WebUrl != web.WebUrl)
                {
                    var evidence = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(file.ProvenanceJson)
                        ?? throw new SnapshotIntegrityException("Physical file provenance must be an evidence array.");
                    file.ProvenanceJson = System.Text.Json.JsonSerializer.Serialize(evidence.Cast<object>().Append(new
                    { source = "SnapshotWebScope", rawSiteUrl = file.SiteUrl, rawWebUrl = file.WebUrl }));
                    file.SiteUrl = web.SiteUrl;
                    file.WebUrl = web.WebUrl;
                }
                var key = (file.SiteCollectionId, file.FileUniqueId);
                files[key] = SPFileWriter.Consolidate(files.GetValueOrDefault(key), file);
                layoutReader.Seed(web.SiteId, file.ServerRelativeUrl, file.CustomizedPageStatus);
            }
            return Task.CompletedTask;
        });
        var uri = new Uri(web.SiteUrl.TrimEnd('/') + (web.WebUrl == "/" ? "" : web.WebUrl));
        using var factory = new ClassicPageDiscoveryJournal(new PnPContextSharePointAspxRestClientFactory(services.ContextFactory, auth), journal);
        using var provider = new SharePointLiveAspxDiscoveryProvider(new("assessment:" + context.AssessmentId, "scheduled-web", "classic-assessment",
            DiscoveryHash.Of("classic-assessment", context.AssessmentId.ToString("D"), web.SiteId.ToString("D"), web.WebId.ToString("D")),
            Intent: ClassicPageModule.Options(context.Options).HomePageOnly ? AspxDiscoveryIntent.HomePageOnly : AspxDiscoveryIntent.FullInventory), factory,
            new(web.SiteId, new Uri(web.SiteUrl), web.WebId, uri, uri.AbsolutePath, web.Template));
        await new AssessmentWebDiscovery(context.AssessmentId, web.SiteUrl, web.WebUrl, writer).RunAsync(provider, token);
        try
        {
            using var pnp = await services.ContextFactory.CreateAsync(uri, auth, token,
                new PnPContextOptions { Properties = new Dictionary<string, object> { ["PipelineAssessmentId"] = context.AssessmentId } });
            using var csom = Csom(pnp, token);
            var enricher = new AspxPageEnricher(pnp, csom.Context, new Uri(web.SiteUrl), uri);
            if (!ClassicPageModule.Options(context.Options).HomePageOnly)
                await enricher.LoadRolesAsync(token);
            foreach (var file in files.Values.Where(f => SharePointAspxUrlPolicy.IsAspxPath(f.ServerRelativeUrl)))
                file.AspxEvidenceJson = await journal.ReadOrAcquireAsync("AspxSource",
                    file.SiteCollectionId.ToString("D") + "|" + file.FileUniqueId.ToString("D"),
                    () => enricher.ReadAsync(ClassicPageSourceJson.Convert<SPFileSource>(file), token), token,
                    json => System.Text.Json.JsonSerializer.Deserialize<AspxPageEvidence>(json)!.SourceReadState == "Available"
                        ? AcquisitionStatus.Complete : AcquisitionStatus.Partial);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            Serilog.Log.Warning(error, "ASPX enrichment context failed for {WebUrl}; retaining discovered files", uri);
            foreach (var file in files.Values.Where(f => SharePointAspxUrlPolicy.IsAspxPath(f.ServerRelativeUrl) && f.AspxEvidenceJson == null))
                file.AspxEvidenceJson = System.Text.Json.JsonSerializer.Serialize(new AspxPageEvidence(
                    "Failed", null, null, "Unresolved", null, AssessmentWebDiscovery.ErrorDetail(error),
                    null, null, null, null, "Failed", DateTime.UtcNow));
        }
        token.ThrowIfCancellationRequested();
        return new(web.SiteUrl, web.WebUrl,
            rows.Values.OrderBy(x => x.RecordKey, StringComparer.Ordinal).Select(ClassicPageSourceJson.Convert<ClassicPageDiscovery>).ToArray(),
            files.Values.OrderBy(x => x.SiteCollectionId).ThenBy(x => x.FileUniqueId)
                .Select(ClassicPageSourceJson.Convert<SPFileSource>).ToArray());
    }

    public async Task<ClassicPageItemSource> ReadPageAsync(ClassicPageWebSource web, ClassicPageDiscovery page, bool skipUsers, CancellationToken token)
    {
        using var pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        using var csom = Csom(pnp, token);
        var input = await ReadPageInputsAsync(pnp, csom.Context, web, page, skipUsers, token);
        var layoutUrl = SnapshotPageLayout.GetPublishingPageLayoutUrl(input.Fields);
        if (!input.MetadataState.Succeeded || layoutUrl == null) return input;
        var layout = await layoutReader.ReadAsync(web.SiteId, new Uri(web.SiteUrl), layoutUrl, async path =>
        {
            var file = csom.Context.Site.RootWeb.GetFileByServerRelativeUrl(path);
            csom.Context.Load(file, value => value.CustomizedPageStatus);
            await csom.Context.ExecuteQueryAsync();
            return file.CustomizedPageStatus.ToString();
        }, token);
        return input with { PublishingLayout = layout };
    }

    internal static async Task<ClassicPageItemSource> ReadPageInputsAsync(PnPContext pnp, ClientContext csom, ClassicPageWebSource web, ClassicPageDiscovery page, bool skipUsers, CancellationToken token)
    {
        var list = pnp.Web.Lists.AsRequested().FirstOrDefault(x => x.Id == page.ListId);
        if (list == null || page.ListItemId == null)
        {
            var roots = pnp.Web.Lists.AsRequested().Select(x => x.RootFolder.ServerRelativeUrl).ToArray();
            var input = EmptyPage(web, page, SourceReadState.NotAttempted) with
            { FileLevelOnly = true, ListRootFolderUrls = roots, OwningListMatched = list != null };
            if (FileLevelPageProjection.FileLevelPageType(SetupPathClassifier.Classify(page.SetupPath)) == null)
                return input;
            try
            {
                var (fileProperties, fileParts) = await PageWebPartSourceReader.ReadAsync(csom, page.Url,
                    ClassicPageRules.WebPartPage, input.Fields, token);
                return input with { FileProperties = fileProperties, WebParts = fileParts, WebPartsState = SourceReadState.Complete };
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            { return input with { WebPartsState = Failure(error) }; }
        }
        var metadata = await ReadPageMetadataAsync(list, page, skipUsers, token);
        if (!metadata.MetadataState.Succeeded) return metadata;
        var fields = metadata.Fields;
        var type = SharePointLiveAspxDiscoveryProvider.IsPublishingPageContentType(fields.GetValueOrDefault("ContentTypeId")?.Text ?? page.ContentTypeId)
            ? ClassicPageRules.PublishingPage : ClassicPageRules.GetPageType(fields.ToDictionary(x => x.Key, x => x.Value.ToValue()));
        var properties = new Dictionary<string, SourceField>();
        ClassicPageWebPartSource[] parts = [];
        var partsState = SourceReadState.NotAttempted;
        if (type is ClassicPageRules.WikiPage or ClassicPageRules.WebPartPage or ClassicPageRules.PublishingPage)
        {
            try
            {
                (properties, parts) = await PageWebPartSourceReader.ReadAsync(csom, page.Url, type, fields, token);
                partsState = SourceReadState.Complete;
            }
            catch (Exception error) when (!token.IsCancellationRequested) { partsState = Failure(error); }
        }
        string? display = null;
        var fallback = SourceReadState.NotAttempted;
        if (!web.CanModernizeState.Succeeded && HomePageDetector.IsHomePage(page.Url, web.WelcomePage) == true)
        {
            try
            {
                var item = csom.Web.GetFileByServerRelativeUrl(page.Url).ListItemAllFields;
                csom.Load(item.ContentType, ct => ct.DisplayFormTemplateName);
                await csom.ExecuteQueryAsync();
                display = item.ContentType.DisplayFormTemplateName;
                fallback = SourceReadState.Complete;
            }
            catch (Exception error) when (!token.IsCancellationRequested) { fallback = Failure(error); }
        }
        token.ThrowIfCancellationRequested();
        return metadata with { FileProperties = properties, WebParts = parts, WebPartsState = partsState,
            ContentTypeDisplayFormTemplateName = display, HomeFallbackState = fallback };
    }

    internal static async Task<ClassicPageItemSource> ReadPageMetadataAsync(SdkList list, ClassicPageDiscovery page,
        bool skipUsers, CancellationToken token)
    {
        if (page.ListId != list.Id || page.ListItemId == null || page.ListItemId <= 0)
            throw new InvalidDataException("Physical page metadata requires its discovered list and list-item identity.");

        var batch = await LoadBatchAsync(list, ClassicPageQuery.Create(new() { "WikiField", "HTML_x0020_File_x0020_Type", "ClientSideApplicationId", "NoExecute" },
            false, page.ListItemId.Value, skipUsers), null, token, page.SiteUrl, page.WebUrl);
        var state = SourceReadState.Complete;
        var fields = new Dictionary<string, SourceField>();
        if (batch.Items.Length != 1 || batch.NextPage != null || !batch.Items[0].TryGetValue("ID", out var id) || id.Text != page.ListItemId.Value.ToString())
            state = new("NotReturned", "The discovered list item was not returned uniquely.");
        else
        {
            fields = batch.Items[0];
            ClassicPageRules.ResolvePhysicalPageUrl(fields.ToDictionary(x => x.Key, x => x.Value.ToValue()), page.Url);
        }
        var physical = ClassicPageSourceJson.Convert<Storage.ClassicPageDiscovery>(page);
        var fileFacts = state.Succeeded
            ? SPFileFactProjection.FromItemMetadata(physical, fields.ToDictionary(x => x.Key, x => x.Value.ToValue()))
            : SPFileFactProjection.FromItemFailure(physical, new InvalidDataException(state.Error ?? state.Status));
        return new(page.SiteUrl, page.WebUrl, page.RecordKey, batch.ListId, batch.ListTitle, batch.ListUrl, fields,
            new(), [], state, SourceReadState.NotAttempted, null, SourceReadState.NotAttempted,
            fileFacts == null ? null : ClassicPageSourceJson.Convert<SPFileSource>(fileFacts),
            ReturnedItems: batch.Items);
    }

    internal static ClassicPageItemSource EmptyPage(ClassicPageWebSource web, ClassicPageDiscovery page, SourceReadState state)
    {
        var facts = state.Status is "Failed" or "Denied" or "NotReturned"
            ? SPFileFactProjection.FromItemFailure(ClassicPageSourceJson.Convert<Storage.ClassicPageDiscovery>(page),
                new InvalidDataException(state.Error ?? state.Status)) : null;
        return new(web.SiteUrl, web.WebUrl, page.RecordKey, page.ListId, null, null, new(), new(), [], state,
            SourceReadState.NotAttempted, null, SourceReadState.NotAttempted,
            facts == null ? null : ClassicPageSourceJson.Convert<SPFileSource>(facts));
    }

    public async Task<ClassicBlogListScopeSource> ReadBlogListsAsync(ClassicPageWebSource web, CancellationToken token)
    {
        using var pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        return await ClassicBlogContentReader.ReadListsAsync(pnp, web, token);
    }

    public async Task<ClassicPageBlogBatchSource> ReadBlogBatchAsync(ClassicPageWebSource web,
        ClassicBlogListSource list, string? paging, bool skipUsers, CancellationToken token)
    {
        using var pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        return await ClassicBlogContentReader.ReadBatchAsync(pnp, web, list, paging, skipUsers, token);
    }

    public async Task<ClassicComponentSource> ReadClassicComponentsAsync(ClassicPageWebSource web, CancellationToken token)
    {
        var options = (ClassicOptions)OptionsBase.FromScannerInput(context.Options);
        options.Pages = false;
        using var pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        using var csom = Csom(pnp, token);
        await ClassicAssessmentMetadata.LoadAsync(pnp, ClassicScanner.AssessmentContextOptions(options), token);
        var scanner = new ClassicScanner(null, services.SiteEnumeration.StorageManager, services.ContextFactory,
            context.AssessmentId, web.SiteUrl, web.WebUrl, web.Template, options);
        await Storage.Pipeline.ClassicComponentStorage.ResetAsync(context.AssessmentId, web, token);
        await ClassicScanner.ExecuteOtherComponentsAsync(scanner, pnp, csom.Context, token);
        return await Storage.Pipeline.ClassicComponentStorage.CaptureAsync(context.AssessmentId, web, token);
    }

    internal static async Task<ClassicPageBlogBatchSource> LoadBatchAsync(SdkList list, string query, string? paging, CancellationToken token,
        string site = "", string web = "")
    {
        token.ThrowIfCancellationRequested(); list.Items.Clear();
        var response = await list.LoadListDataAsStreamAsync(new PnP.Core.Model.SharePoint.RenderListDataOptions
        { ViewXml = query, RenderOptions = RenderListDataOptionsFlags.ListData, Paging = paging });
        token.ThrowIfCancellationRequested();
        return new(site, web, list.Id, list.Title, list.RootFolder.ServerRelativeUrl,
            list.Items.AsRequested().Select(ItemFields).ToArray(),
            response.TryGetValue("NextHref", out var next) ? next?.ToString()?.TrimStart('?') : null, SourceReadState.Complete);
    }
    internal static Dictionary<string, SourceField> Fields(IDictionary<string, object> fields) => fields.ToDictionary(x => x.Key, x => Field(x.Value), StringComparer.Ordinal);
    internal static Dictionary<string, SourceField> ItemFields(IListItem item)
    {
        var fields = Fields(item.Values);
        // RenderListDataAsStream puts ID in the modeled Id property and deliberately omits it
        // from Values. Persist that returned identity along with the detached field values.
        if (fields.TryGetValue("ID", out var existing) && existing.ToValue() is int fieldId && fieldId != item.Id)
            throw new InvalidDataException("The list item's modeled ID differs from its returned ID field.");
        fields["ID"] = SourceField.Of("Int32", item.Id);
        return fields;
    }
    private static SourceField Field(object? value) => value switch
    {
        null => SourceField.Of("Null", null),
        IFieldUserValue user => SourceField.Of("User", new { user.Email, user.LookupValue, user.LookupId }),
        IFieldLookupValue lookup => SourceField.Of("Lookup", new { lookup.LookupValue, lookup.LookupId }),
        Microsoft.SharePoint.Client.FieldUserValue user => SourceField.Of("User", new { Email = user.Email, user.LookupValue, user.LookupId }),
        Microsoft.SharePoint.Client.FieldUrlValue url => SourceField.Of("Url", new { url.Url, url.Description }),
        IFieldUrlValue url => SourceField.Of("Url", new { url.Url, url.Description }),
        DateTime date => SourceField.Of("DateTime", date), Guid guid => SourceField.Of("Guid", guid),
        int number => SourceField.Of("Int32", number), long number => SourceField.Of("Int64", number),
        double number => SourceField.Of("Double", number), decimal number => SourceField.Of("Decimal", number),
        bool boolean => SourceField.Of("Boolean", boolean), string text => SourceField.Of("String", text),
        System.Text.Json.JsonElement json => SourceField.Of("Json", json),
        _ => throw new NotSupportedException($"Unsupported source field type '{value.GetType().FullName}'."),
    };
    public Task CollectAuditAsync(ClassicPageScopeSource scope, ClassicPageAcquisitionJournal journal, CancellationToken token) =>
        new ClassicPageAuditCollector(context.Options, services.Authentication).CollectAsync(scope, journal, token);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
