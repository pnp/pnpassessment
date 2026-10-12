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
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using AuthenticationManager = PnP.Scanning.Core.Authentication.AuthenticationManager;
using SdkList = PnP.Core.Model.SharePoint.IList;
using ClassicPageDiscovery = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageDiscoveryRow;

namespace PnP.Scanning.Core.Pipeline.Collection.Module;

/// <summary>
/// Defines the online scope and evidence reads needed by the Classic Pages collector, returning detached source DTOs.
/// </summary>
internal interface IClassicPageOnlineSource : IAsyncDisposable
{
    /// <summary>
    /// Enumerates the selected sites and fixes the collection options, audit interval and scope evidence.
    /// </summary>
    Task<ClassicPageScopeSource> ReadSitesAsync(ClassicPageSourceOptions options, CancellationToken token);
    /// <summary>
    /// Enumerates one site collection into web identities and templates, retaining its read status.
    /// </summary>
    Task<ClassicPageSiteScope> ReadWebsAsync(string siteUrl, CancellationToken token);
    /// <summary>
    /// Reads the minimum site and web identity required before discovery, without optional enrichment.
    /// </summary>
    Task<ClassicPageWebSource> ReadIdentityAsync(string siteUrl, string webUrl, string template, CancellationToken token);
    /// <summary>
    /// Reads web metadata and acquisition states needed by page classification and home-page analysis.
    /// </summary>
    Task<ClassicPageWebSource> ReadWebAsync(string siteUrl, string webUrl, string template, CancellationToken token);
    /// <summary>
    /// Discovers physical ASPX files and scope evidence through the resumable request journal.
    /// </summary>
    Task<ClassicPageDiscoverySource> DiscoverAsync(ClassicPageWebSource web, ClassicPageAcquisitionJournal journal, CancellationToken token);
    /// <summary>
    /// Reads one discovered page item and its web part and home-page evidence into detached values.
    /// </summary>
    Task<ClassicPageItemSource> ReadPageAsync(ClassicPageWebSource web, ClassicPageDiscovery page, bool skipUsers, CancellationToken token);
    /// <summary>
    /// Reads one batch of blog items and its continuation, or returns null when no Posts list exists.
    /// </summary>
    Task<ClassicPageBlogBatchSource?> ReadBlogBatchAsync(ClassicPageWebSource web, string? paging, bool skipUsers, CancellationToken token);
    /// <summary>
    /// Collects audit response receipts for the fixed scope and audit interval.
    /// </summary>
    Task CollectAuditAsync(ClassicPageScopeSource scope, ClassicPageAcquisitionJournal journal, CancellationToken token);
}

/// <summary>
/// Owns authenticated PnP, CSOM and Graph acquisition and converts SDK responses to persistable Classic Pages inputs.
/// <para>Evolved from <c>Scanners/Classic/PageScanComponent.cs</c> to persist acquisition facts separately from classification and report generation.</para>
/// </summary>
internal sealed class ClassicPageOnlineSource(CollectionContext context, CollectionServices services) : IClassicPageOnlineSource
{
    private readonly System.Collections.Concurrent.ConcurrentBag<HttpClient> collectionClients = new();
    private readonly ExternalAuthenticationProvider auth = new((_, scopes) => services.Authentication.GetAccessTokenAsync(scopes));
    private PnPContextOptions ContextOptions => new()
    {
        Properties = new Dictionary<string, object> { ["PipelineAssessmentId"] = context.AssessmentId },
        AdditionalSitePropertiesOnCreate = new Expression<Func<ISite, object>>[] { s => s.Features.QueryProperties(f => f.DefinitionId) },
        AdditionalWebPropertiesOnCreate = new Expression<Func<IWeb, object>>[]
        {
            w => w.LastItemUserModifiedDate, w => w.Features.QueryProperties(f => f.DefinitionId),
            w => w.Lists.QueryProperties(l => l.Id, l => l.Title, l => l.TemplateType, l => l.RootFolder.QueryProperties(f => f.ServerRelativeUrl)),
        },
    };
    /// <summary>
    /// Creates a PnP context for the selected web with collection-specific authentication and modeled property requests.
    /// </summary>
    private Task<PnPContext> OpenAsync(string site, string web, CancellationToken token) =>
        services.ContextFactory.CreateAsync(new Uri(site.TrimEnd('/') + (web == "/" ? "" : web)), auth, token, ContextOptions);
    /// <summary>
    /// Creates a bearer-authenticated CSOM context whose transport observes the collection cancellation token.
    /// </summary>
    private ClientContext Csom(PnPContext pnp, CancellationToken token)
    {
        var client = new ClientContext(pnp.Uri) { DisableReturnValueCache = true };
        client.ExecutingWebRequest += (_, args) =>
        {
            token.ThrowIfCancellationRequested();
            args.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + services.Authentication
                .GetAccessTokenAsync(new[] { pnp.Uri.GetLeftPart(UriPartial.Authority) + "/.default" }).GetAwaiter().GetResult();
        };
        var transport = new HttpClient(new CollectionHttpClient(pnp.RestClient.Client, token)) { Timeout = Timeout.InfiniteTimeSpan };
        collectionClients.Add(transport);
        client.WebRequestExecutorFactory = new HttpClientWebRequestExecutorFactory(transport);
        return client;
    }
    /// <summary>
    /// Classifies a read exception as denied or failed and retains the underlying diagnostic message.
    /// </summary>
    internal static SourceReadState Failure(Exception error) => new(
        AssessmentWebDiscovery.Classify(error) == DiscoveryTerminalOutcome.Denied ? "Denied" : "Failed", error.GetType().Name + ": " + error.GetBaseException().Message);

    /// <summary>
    /// Reuses site enumeration to capture the fixed site list, scope evidence, page options and UTC audit window.
    /// <para>Evolved from <c>Services/SiteEnumerationManager.cs</c> to capture existing enumeration results as fixed snapshot inputs.</para>
    /// </summary>
    public async Task<ClassicPageScopeSource> ReadSitesAsync(ClassicPageSourceOptions options, CancellationToken token)
    {
        var request = context.Options.Clone();
        request.Mode = Mode.Classic.ToString();
        request.Properties.Clear();
        ClassicStartRequestBuilder.AddClassicProperties(request, new[] { ClassicComponent.Pages }, options.ExportWebPartProperties,
            options.SkipUsageInformation, options.SkipUserInformation, options.HomePageOnly, options.AuditLogWindowDays);
        var evidence = new List<Storage.ClassicPageDiscovery>();
        var sites = await services.SiteEnumeration.EnumerateSiteCollectionsToScanAsync(request, services.Authentication,
            message => Serilog.Log.Information("{Message}", message), evidence, token);
        token.ThrowIfCancellationRequested();
        foreach (var row in evidence) row.ScanId = context.AssessmentId;
        var end = DateTime.UtcNow;
        return new(sites.ToArray(), [], !string.IsNullOrWhiteSpace(request.SitesList) || !string.IsNullOrWhiteSpace(request.SitesFile),
            options, end.AddDays(-options.AuditLogWindowDays), end, evidence.Select(ClassicPageSourceJson.Convert<ClassicPageDiscovery>).ToArray());
    }

    /// <summary>
    /// Enumerates web URLs and templates for a site and persists an explicit terminal read state on failure.
    /// <para>Evolved from <c>Services/SiteEnumerationManager.cs</c> to capture existing enumeration results as fixed snapshot inputs.</para>
    /// </summary>
    public async Task<ClassicPageSiteScope> ReadWebsAsync(string siteUrl, CancellationToken token)
    {
        try
        {
            var result = await services.SiteEnumeration.EnumerateWebsToScanAsync(context.AssessmentId, siteUrl,
                new ClassicOptions { Pages = true }, services.Authentication, isRestart: false, cancellationToken: token, contextPropertyKey: "PipelineAssessmentId");
            token.ThrowIfCancellationRequested();
            return new(siteUrl, result.Webs.Select(x => x.WebUrl).ToArray(), result.Webs.Select(x => x.WebTemplate).ToArray(), SourceReadState.Complete);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { return new(siteUrl, [], [], Failure(error)); }
    }

    /// <summary>
    /// Opens the selected web and reads its captured features, welcome page and home-page API or fallback facts.
    /// </summary>
    public async Task<ClassicPageWebSource> ReadWebAsync(string siteUrl, string webUrl, string template, CancellationToken token)
    {
        using var pnp = await OpenAsync(siteUrl, webUrl, token);
        await pnp.Site.GetAsync(s => s.Id);
        await pnp.Web.GetAsync(w => w.Id);
        using var csom = Csom(pnp, token);
        return await ReadWebFactsAsync(pnp, csom, siteUrl, webUrl, template, token);
    }

    /// <summary>
    /// Reads site and web IDs with a minimal context so optional metadata failures do not prevent discovery.
    /// </summary>
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

    /// <summary>
    /// Captures web features and home-page API or fallback inputs, recording each optional read status independently.
    /// <para>Evolved from <c>Scanners/Classic/PageScanComponent.cs</c> to persist acquisition facts separately from classification and report generation.</para>
    /// </summary>
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

    /// <summary>
    /// Runs the existing ASPX discovery provider through journaled requests and returns detached discovery rows for the selected web.
    /// <para>Evolved from <c>Scanners/Classic/ClassicPageDiscoveryComponent.cs</c> to reuse ASPX discovery with durable request receipts in collection.</para>
    /// </summary>
    public async Task<ClassicPageDiscoverySource> DiscoverAsync(ClassicPageWebSource web, ClassicPageAcquisitionJournal journal, CancellationToken token)
    {
        var rows = new Dictionary<string, Storage.ClassicPageDiscovery>(StringComparer.Ordinal);
        var writer = new AssessmentDiscoveryWriter((batch, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            foreach (var row in batch)
            {
                if (rows.TryGetValue(row.RecordKey, out var previous)) AssessmentDiscoveryWriter.Reconcile(previous, row);
                rows[row.RecordKey] = row;
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
        return new(web.SiteUrl, web.WebUrl, rows.Values.OrderBy(x => x.RecordKey, StringComparer.Ordinal).Select(ClassicPageSourceJson.Convert<ClassicPageDiscovery>).ToArray());
    }

    /// <summary>
    /// Opens the owning web and reads a discovered physical page using its fixed list and item identity.
    /// <para>Evolved from <c>Scanners/Classic/PageScanComponent.cs</c> to persist acquisition facts separately from classification and report generation.</para>
    /// </summary>
    public async Task<ClassicPageItemSource> ReadPageAsync(ClassicPageWebSource web, ClassicPageDiscovery page, bool skipUsers, CancellationToken token)
    {
        if (page.ListId == null || page.ListItemId == null)
            return EmptyPage(web, page, SourceReadState.NotAttempted);
        using var pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        using var csom = Csom(pnp, token);
        return await ReadPageInputsAsync(pnp, csom, web, page, skipUsers, token);
    }

    /// <summary>
    /// Collects page fields, web part definitions and fallback facts while retaining metadata and enrichment failures separately.
    /// <para>Evolved from <c>Scanners/Classic/PageScanComponent.cs</c> to persist acquisition facts separately from classification and report generation.</para>
    /// </summary>
    internal static async Task<ClassicPageItemSource> ReadPageInputsAsync(PnPContext pnp, ClientContext csom, ClassicPageWebSource web, ClassicPageDiscovery page, bool skipUsers, CancellationToken token)
    {
        if (page.ListId == null || page.ListItemId == null) return EmptyPage(web, page, SourceReadState.NotAttempted);
        var list = pnp.Web.Lists.AsRequested().FirstOrDefault(x => x.Id == page.ListId);
        if (list == null) return EmptyPage(web, page, new("NotReturned", "The discovered owning list was not returned."));
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

    /// <summary>
    /// Reads the exact discovered list item and rejects missing, duplicate or mismatched identity and file URL responses.
    /// <para>Evolved from <c>Scanners/Classic/PageScanComponent.cs</c> to persist acquisition facts separately from classification and report generation.</para>
    /// </summary>
    internal static async Task<ClassicPageItemSource> ReadPageMetadataAsync(SdkList list, ClassicPageDiscovery page,
        bool skipUsers, CancellationToken token)
    {
        if (page.ListId != list.Id || page.ListItemId == null || page.ListItemId <= 0)
            throw new InvalidDataException("Physical page metadata requires its discovered list and list-item identity.");

        var batch = await LoadBatchAsync(list, ClassicPageQuery.Create(new() { "WikiField", "HTML_x0020_File_x0020_Type", "ClientSideApplicationId" },
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
        return new(page.SiteUrl, page.WebUrl, page.RecordKey, batch.ListId, batch.ListTitle, batch.ListUrl, fields,
            new(), [], state, SourceReadState.NotAttempted, null, SourceReadState.NotAttempted);
    }

    /// <summary>
    /// Creates an explicit failed or unavailable page source without inventing metadata or web part content.
    /// </summary>
    internal static ClassicPageItemSource EmptyPage(ClassicPageWebSource web, ClassicPageDiscovery page, SourceReadState state) =>
        new(web.SiteUrl, web.WebUrl, page.RecordKey, page.ListId, null, null, new(), new(), [], state,
            SourceReadState.NotAttempted, null, SourceReadState.NotAttempted);

    /// <summary>
    /// Finds the web Posts list and captures one requested blog batch with its paging continuation.
    /// <para>Evolved from <c>Scanners/Classic/PageScanComponent.cs</c> to persist acquisition facts separately from classification and report generation.</para>
    /// </summary>
    public async Task<ClassicPageBlogBatchSource?> ReadBlogBatchAsync(ClassicPageWebSource web, string? paging, bool skipUsers, CancellationToken token)
    {
        using var pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        var list = pnp.Web.Lists.AsRequested().FirstOrDefault(x => x.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.Posts);
        return list == null ? null : await LoadBatchAsync(list, ClassicPageQuery.Create(new(), false, null, skipUsers), paging, token, web.SiteUrl, web.WebUrl);
    }

    /// <summary>
    /// Loads one modeled list data response and captures only its returned items, fields and next-page token.
    /// <para>Evolved from <c>Scanners/Classic/PageScanComponent.cs</c> to persist acquisition facts separately from classification and report generation.</para>
    /// </summary>
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
    /// <summary>
    /// Converts a returned SDK property bag into detached, typed JSON field values.
    /// </summary>
    internal static Dictionary<string, SourceField> Fields(IDictionary<string, object> fields) => fields.ToDictionary(x => x.Key, x => Field(x.Value), StringComparer.Ordinal);
    /// <summary>
    /// Captures modeled item fields and adds the returned Id, rejecting conflicts with an explicit ID field.
    /// </summary>
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
    /// <summary>
    /// Converts SDK user, URL and scalar values to stable typed source fields without retaining SDK objects.
    /// </summary>
    private static SourceField Field(object? value) => value switch
    {
        null => SourceField.Of("Null", null),
        IFieldUserValue user => SourceField.Of("User", new { user.Email, user.LookupValue, user.LookupId }),
        Microsoft.SharePoint.Client.FieldUserValue user => SourceField.Of("User", new { Email = user.Email, user.LookupValue, user.LookupId }),
        Microsoft.SharePoint.Client.FieldUrlValue url => SourceField.Of("Url", new { url.Url, url.Description }),
        IFieldUrlValue url => SourceField.Of("Url", new { url.Url, url.Description }),
        DateTime date => SourceField.Of("DateTime", date), Guid guid => SourceField.Of("Guid", guid),
        int number => SourceField.Of("Int32", number), bool boolean => SourceField.Of("Boolean", boolean),
        _ => SourceField.Of("String", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)),
    };
    /// <summary>
    /// Invokes the Graph audit collector using collection-only authentication and the durable acquisition journal.
    /// <para>Evolved from <c>Storage/StorageManager.cs</c> to move audit acquisition out of report storage and into the collection phase.</para>
    /// </summary>
    public Task CollectAuditAsync(ClassicPageScopeSource scope, ClassicPageAcquisitionJournal journal, CancellationToken token) =>
        new ClassicPageAuditCollector(context.Options, services.Authentication).CollectAsync(scope, journal, token);
    /// <summary>
    /// Disposes the collection HTTP transports created for its CSOM contexts.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        foreach (var client in collectionClients) client.Dispose();
        return ValueTask.CompletedTask;
    }
}
