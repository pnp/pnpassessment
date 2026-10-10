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
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Pipeline.Contracts;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using AuthenticationManager = PnP.Scanning.Core.Authentication.AuthenticationManager;
using SdkList = PnP.Core.Model.SharePoint.IList;
using ClassicPageDiscovery = PnP.Scanning.Core.Pipeline.Contracts.ClassicPageDiscoveryRow;

namespace PnP.Scanning.Core.Pipeline.Collection;

internal interface IClassicPageOnlineSource : IAsyncDisposable
{
    Task<ClassicPageScopeSource> ReadSitesAsync(ClassicPageSourceOptions options, CancellationToken token);
    Task<ClassicPageSiteScope> ReadWebsAsync(string siteUrl, CancellationToken token);
    Task<ClassicPageWebSource> ReadIdentityAsync(string siteUrl, string webUrl, string template, CancellationToken token);
    Task<ClassicPageWebSource> ReadWebAsync(string siteUrl, string webUrl, string template, CancellationToken token);
    Task<ClassicPageDiscoverySource> DiscoverAsync(ClassicPageWebSource web, ClassicPageAcquisitionJournal journal, CancellationToken token);
    Task<ClassicPageItemSource> ReadPageAsync(ClassicPageWebSource web, ClassicPageDiscovery page, bool skipUsers, CancellationToken token);
    Task<ClassicPageBlogBatchSource?> ReadBlogBatchAsync(ClassicPageWebSource web, string? paging, bool skipUsers, CancellationToken token);
    Task CollectAuditAsync(ClassicPageScopeSource scope, ClassicPageAcquisitionJournal journal, CancellationToken token);
}

/// <summary>All PnP/CSOM/Graph objects live here and are disposed before offline analysis.</summary>
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
    private Task<PnPContext> OpenAsync(string site, string web, CancellationToken token) =>
        services.ContextFactory.CreateAsync(new Uri(site.TrimEnd('/') + (web == "/" ? "" : web)), auth, token, ContextOptions);
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
    internal static SourceReadState Failure(Exception error) => new(
        AssessmentWebDiscovery.Classify(error) == DiscoveryTerminalOutcome.Denied ? "Denied" : "Failed", error.GetType().Name + ": " + error.GetBaseException().Message);

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

    public async Task<ClassicPageWebSource> ReadWebAsync(string siteUrl, string webUrl, string template, CancellationToken token)
    {
        using var pnp = await OpenAsync(siteUrl, webUrl, token);
        await pnp.Site.GetAsync(s => s.Id);
        await pnp.Web.GetAsync(w => w.Id);
        using var csom = Csom(pnp, token);
        return await ReadWebFactsAsync(pnp, csom, siteUrl, webUrl, template, token);
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

    public async Task<ClassicPageItemSource> ReadPageAsync(ClassicPageWebSource web, ClassicPageDiscovery page, bool skipUsers, CancellationToken token)
    {
        if (page.ListId == null || page.ListItemId == null)
            return EmptyPage(web, page, SourceReadState.NotAttempted);
        using var pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        using var csom = Csom(pnp, token);
        return await ReadPageInputsAsync(pnp, csom, web, page, skipUsers, token);
    }

    internal static async Task<ClassicPageItemSource> ReadPageInputsAsync(PnPContext pnp, ClientContext csom, ClassicPageWebSource web, ClassicPageDiscovery page, bool skipUsers, CancellationToken token)
    {
        if (page.ListId == null || page.ListItemId == null) return EmptyPage(web, page, SourceReadState.NotAttempted);
        var list = pnp.Web.Lists.AsRequested().FirstOrDefault(x => x.Id == page.ListId);
        if (list == null) return EmptyPage(web, page, new("NotReturned", "The discovered owning list was not returned."));
        var batch = await LoadBatchAsync(list, PageScanComponent.PageQuery(new() { "WikiField", "HTML_x0020_File_x0020_Type", "ClientSideApplicationId" },
            false, page.ListItemId.Value, skipUsers), null, token);
        if (batch.Items.Length != 1 || !batch.Items[0].TryGetValue("ID", out var id) || id.Text != page.ListItemId.Value.ToString())
            return EmptyPage(web, page, new("NotReturned", "The discovered list item was not returned uniquely."));
        var fields = batch.Items[0];
        ClassicPageRules.ResolvePhysicalPageUrl(fields.ToDictionary(x => x.Key, x => x.Value.ToValue()), page.Url);
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
        return new(web.SiteUrl, web.WebUrl, page.RecordKey, list.Id, list.Title, list.RootFolder.ServerRelativeUrl, fields,
            properties, parts, SourceReadState.Complete, partsState, display, fallback);
    }

    internal static ClassicPageItemSource EmptyPage(ClassicPageWebSource web, ClassicPageDiscovery page, SourceReadState state) =>
        new(web.SiteUrl, web.WebUrl, page.RecordKey, page.ListId, null, null, new(), new(), [], state,
            SourceReadState.NotAttempted, null, SourceReadState.NotAttempted);

    public async Task<ClassicPageBlogBatchSource?> ReadBlogBatchAsync(ClassicPageWebSource web, string? paging, bool skipUsers, CancellationToken token)
    {
        using var pnp = await OpenAsync(web.SiteUrl, web.WebUrl, token);
        var list = pnp.Web.Lists.AsRequested().FirstOrDefault(x => x.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.Posts);
        return list == null ? null : await LoadBatchAsync(list, PageScanComponent.PageQuery(new(), false, null, skipUsers), paging, token, web.SiteUrl, web.WebUrl);
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
        Microsoft.SharePoint.Client.FieldUserValue user => SourceField.Of("User", new { Email = user.Email, user.LookupValue, user.LookupId }),
        Microsoft.SharePoint.Client.FieldUrlValue url => SourceField.Of("Url", new { url.Url, url.Description }),
        IFieldUrlValue url => SourceField.Of("Url", new { url.Url, url.Description }),
        DateTime date => SourceField.Of("DateTime", date), Guid guid => SourceField.Of("Guid", guid),
        int number => SourceField.Of("Int32", number), bool boolean => SourceField.Of("Boolean", boolean),
        _ => SourceField.Of("String", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)),
    };
    public Task CollectAuditAsync(ClassicPageScopeSource scope, ClassicPageAcquisitionJournal journal, CancellationToken token) =>
        new ClassicPageAuditCollector(context.Options, services.Authentication).CollectAsync(scope, journal, token);
    public ValueTask DisposeAsync()
    {
        foreach (var client in collectionClients) client.Dispose();
        return ValueTask.CompletedTask;
    }
}
