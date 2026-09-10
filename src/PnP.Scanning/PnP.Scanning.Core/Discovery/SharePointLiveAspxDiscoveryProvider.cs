using PnP.Core.Model.SharePoint;
using PnP.Core.Services;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal sealed record SharePointLiveAspxDiscoveryOptions(
    IReadOnlyList<Uri> SiteCollectionUrls,
    string PermissionContext,
    string VisibilityBoundary,
    string AuthorityRevision,
    string AuthorityHash);

internal sealed record SharePointRestPage(
    Uri RequestUri,
    HttpStatusCode StatusCode,
    IReadOnlyList<JsonElement> Items,
    string NextLink,
    string ResponseDigest,
    string SchemaFlavor,
    DiscoveryTerminalOutcome Outcome,
    string ErrorCode = null);

internal sealed record SharePointResolvedFile(
    DiscoveryTerminalOutcome Outcome,
    string FileUniqueId,
    string Name,
    string ServerRelativeUrl,
    string CustomizedPageStatus,
    string EvidenceRef,
    string ErrorCode = null);

internal interface ISharePointAspxRestClient : IDisposable
{
    Uri WebUrl { get; }
    Task<SharePointRestPage> GetPageAsync(Uri requestUri, CancellationToken cancellationToken = default);
    Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl,
        CancellationToken cancellationToken = default);
}

internal interface ISharePointAspxRestClientFactory : IDisposable
{
    Task<ISharePointAspxRestClient> GetAsync(Uri webUrl, CancellationToken cancellationToken = default);
}

internal sealed class PnPContextSharePointAspxRestClientFactory : ISharePointAspxRestClientFactory
{
    private readonly IPnPContextFactory contextFactory;
    private readonly IAuthenticationProvider authenticationProvider;
    private readonly Dictionary<string, ISharePointAspxRestClient> clients = new(StringComparer.OrdinalIgnoreCase);

    internal PnPContextSharePointAspxRestClientFactory(IPnPContextFactory contextFactory,
        IAuthenticationProvider authenticationProvider)
    {
        this.contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        this.authenticationProvider = authenticationProvider ?? throw new ArgumentNullException(nameof(authenticationProvider));
    }

    public async Task<ISharePointAspxRestClient> GetAsync(Uri webUrl,
        CancellationToken cancellationToken = default)
    {
        var key = webUrl.AbsoluteUri.TrimEnd('/');
        if (clients.TryGetValue(key, out var existing)) return existing;
        var context = await contextFactory.CreateAsync(webUrl, authenticationProvider, cancellationToken,
            new PnPContextOptions()).ConfigureAwait(false);
        var client = new PnPContextSharePointAspxRestClient(context);
        clients.Add(key, client);
        return client;
    }

    public void Dispose()
    {
        foreach (var client in clients.Values) client.Dispose();
        clients.Clear();
    }
}

internal sealed class PnPContextSharePointAspxRestClient : ISharePointAspxRestClient
{
    private readonly PnPContext context;

    internal PnPContextSharePointAspxRestClient(PnPContext context) =>
        this.context = context ?? throw new ArgumentNullException(nameof(context));

    public Uri WebUrl => context.Uri;

    public async Task<SharePointRestPage> GetPageAsync(Uri requestUri,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json;odata=nometadata");
        await context.AuthenticationProvider.AuthenticateRequestAsync(context.Uri, request).ConfigureAwait(false);
        using var response = await context.RestClient.Client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var outcome = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? DiscoveryTerminalOutcome.Denied
            : response.IsSuccessStatusCode ? DiscoveryTerminalOutcome.Complete : DiscoveryTerminalOutcome.Failed;
        if (!response.IsSuccessStatusCode)
            return new(requestUri, response.StatusCode, Array.Empty<JsonElement>(), null, digest, "http-error",
                outcome, "http_" + (int)response.StatusCode);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var parsed = ParseEnvelope(document.RootElement);
            return new(requestUri, response.StatusCode, parsed.Items, parsed.NextLink, digest,
                parsed.SchemaFlavor, DiscoveryTerminalOutcome.Complete);
        }
        catch (JsonException)
        {
            return new(requestUri, response.StatusCode, Array.Empty<JsonElement>(), null, digest,
                "invalid-json", DiscoveryTerminalOutcome.Failed, "response_json_invalid");
        }
    }

    public async Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await context.Web.GetFileByServerRelativeUrlOrDefaultAsync(serverRelativeUrl,
                item => item.UniqueId, item => item.Name, item => item.ServerRelativeUrl,
                item => item.CustomizedPageStatus).ConfigureAwait(false);
            if (file == null)
                return new(DiscoveryTerminalOutcome.Failed, null, null, serverRelativeUrl, null,
                    "PnP.Core:IWeb.GetFileByServerRelativeUrlOrDefaultAsync", "locator_not_found");
            return new(DiscoveryTerminalOutcome.Complete, file.UniqueId.ToString("D"), file.Name,
                file.ServerRelativeUrl, file.CustomizedPageStatus.ToString(),
                "PnP.Core:IWeb.GetFileByServerRelativeUrlOrDefaultAsync");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var denied = ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
            return new(denied ? DiscoveryTerminalOutcome.Denied : DiscoveryTerminalOutcome.Failed,
                null, null, serverRelativeUrl, null,
                "PnP.Core:IWeb.GetFileByServerRelativeUrlOrDefaultAsync",
                denied ? "locator_denied" : "locator_resolution_failed");
        }
    }

    public void Dispose() => context.Dispose();

    private static (IReadOnlyList<JsonElement> Items, string NextLink, string SchemaFlavor) ParseEnvelope(
        JsonElement root)
    {
        if (TryProperty(root, "value", out var value) && value.ValueKind == JsonValueKind.Array)
            return (value.EnumerateArray().Select(item => item.Clone()).ToArray(),
                String(root, "@odata.nextLink") ?? String(root, "odata.nextLink"), "odata-nometadata");
        if (TryProperty(root, "d", out var verbose))
        {
            if (TryProperty(verbose, "results", out var results) && results.ValueKind == JsonValueKind.Array)
                return (results.EnumerateArray().Select(item => item.Clone()).ToArray(),
                    String(verbose, "__next"), "odata-verbose");
            return (new[] { verbose.Clone() }, String(verbose, "__next"), "odata-verbose-object");
        }
        if (root.ValueKind == JsonValueKind.Array)
            return (root.EnumerateArray().Select(item => item.Clone()).ToArray(), null, "json-array");
        return (new[] { root.Clone() }, String(root, "@odata.nextLink") ?? String(root, "odata.nextLink") ??
            String(root, "__next"), "json-object");
    }

    internal static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    internal static string String(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ToString() : null;
}

/// <summary>
/// Authentication/context-backed live SharePoint acquisition provider. All authority collections are
/// requested without BaseType, Hidden, catalog, template or page-family admission filters.
/// </summary>
internal sealed class SharePointLiveAspxDiscoveryProvider : IAspxDiscoveryProvider, IAspxReferenceAcquisitionProvider
{
    private const string AllListsSelect = "Id,Title,BaseType,BaseTemplate,Hidden,IsCatalog,RootFolder/ServerRelativeUrl,DefaultViewUrl";
    private const string FormsSelect = "Id,ServerRelativeUrl,FormType";
    private const string ViewsSelect = "Id,ServerRelativeUrl,Hidden,DefaultView,PersonalView,Title";
    private const string FilesSelect = "UniqueId,Name,ServerRelativeUrl,CustomizedPageStatus";
    private const string FoldersSelect = "UniqueId,Name,ServerRelativeUrl";
    private const string WebsSelect = "Id,Url,ServerRelativeUrl,Title,WebTemplate,Configuration";
    private readonly SharePointLiveAspxDiscoveryOptions options;
    private readonly ISharePointAspxRestClientFactory clientFactory;
    private readonly Dictionary<string, LiveScope> scopes = new(StringComparer.Ordinal);
    private bool disposed;

    internal SharePointLiveAspxDiscoveryProvider(SharePointLiveAspxDiscoveryOptions options,
        ISharePointAspxRestClientFactory clientFactory)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        if (options.SiteCollectionUrls == null || options.SiteCollectionUrls.Count == 0)
            throw new ArgumentException("At least one site collection URL is required.", nameof(options));
        if (options.SiteCollectionUrls.Any(url => url == null || !url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Every site collection URL must be an absolute HTTPS URL.", nameof(options));
        var root = new LiveScope("tenant", null, DiscoveryScopeKind.Tenant, null,
            "sharepoint-live://declared-tenant-boundary", "tenant", null, null, null, null, null);
        scopes.Add(root.ScopeKey, root);
        RootScope = Registration(root);
    }

    public DiscoveryScopeRegistration RootScope { get; }
    public AspxReferenceCollector ReferenceCollector { get; } = new();

    public Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(DiscoveryScopeRegistration parent,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var scope = scopes[parent.ScopeKey];
        return scope.Kind switch
        {
            DiscoveryScopeKind.Tenant => EnumerateTenantAsync(scope, cancellationToken),
            DiscoveryScopeKind.Geo => EnumerateGeoAsync(scope, cancellationToken),
            DiscoveryScopeKind.SiteCollection => EnumerateSiteCollectionAsync(scope, cancellationToken),
            DiscoveryScopeKind.Web => EnumerateWebAsync(scope, cancellationToken),
            DiscoveryScopeKind.Container => EnumerateContainerAsync(scope, cancellationToken),
            DiscoveryScopeKind.Folder => EnumerateFolderAsync(scope, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(parent), parent.Kind, null),
        };
    }

    public IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var scope = scopes[surface.ScopeKey];
        if (scope.SourceKind == null) return null;
        return new LiveRawDiscoverySource(token => scope.Role switch
        {
            "folder" => ReadFolderFilesAsync(scope, token),
            "forms" => ReadReferencesAsync(scope, isForm: true, token),
            "views" => ReadReferencesAsync(scope, isForm: false, token),
            _ => throw new InvalidOperationException($"Unsupported live raw surface role '{scope.Role}'."),
        });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        clientFactory.Dispose();
    }

    private Task<DiscoveryChildEnumerationResult> EnumerateTenantAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var geo = Add(new LiveScope(Key("geo", "declared"), parent.ScopeKey, DiscoveryScopeKind.Geo, null,
            "sharepoint-live://declared-geo", "geo", null, null, null, null, null));
        return Task.FromResult(Result(parent, new[] { geo }, DiscoveryTerminalOutcome.Complete));
    }

    private Task<DiscoveryChildEnumerationResult> EnumerateGeoAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sites = options.SiteCollectionUrls.DistinctBy(url => url.AbsoluteUri.TrimEnd('/'),
                StringComparer.OrdinalIgnoreCase)
            .Select(url => Add(new LiveScope(Key("site", url.AbsoluteUri), parent.ScopeKey,
                DiscoveryScopeKind.SiteCollection, null, url.AbsoluteUri.TrimEnd('/'), "site", url,
                null, null, null, null))).ToArray();
        return Task.FromResult(Result(parent, sites, DiscoveryTerminalOutcome.Complete));
    }

    private async Task<DiscoveryChildEnumerationResult> EnumerateSiteCollectionAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        var observed = new Dictionary<string, LiveScope>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Uri>();
        queue.Enqueue(parent.WebUrl);
        var outcome = DiscoveryTerminalOutcome.Complete;
        while (queue.TryDequeue(out var webUrl))
        {
            var canonical = webUrl.AbsoluteUri.TrimEnd('/');
            if (observed.ContainsKey(canonical)) continue;
            observed.Add(canonical, Add(new LiveScope(Key("web", canonical), parent.ScopeKey,
                DiscoveryScopeKind.Web, null, canonical, "web", webUrl, null, null, null, null)));
            var endpoint = Endpoint(webUrl, $"_api/web/webs?$select={WebsSelect}");
            var result = await ReadCollectionAsync(parent.ScopeKey, parent.ScopeKey, "subwebs:" + Key("endpoint", canonical),
                endpoint, WebsSelect, string.Empty, AspxSurfaceApplicability.Applicable,
                "RootAndSubwebAuthority", cancellationToken).ConfigureAwait(false);
            outcome = MergeOutcome(outcome, result.Validation.Outcome);
            foreach (var item in result.Items)
            {
                var value = PropertyString(item, "Url");
                if (Uri.TryCreate(value, UriKind.Absolute, out var child)) queue.Enqueue(child);
            }
        }
        return Result(parent, observed.Values.OrderBy(item => item.ScopeKey, StringComparer.Ordinal).ToArray(), outcome);
    }

    private async Task<DiscoveryChildEnumerationResult> EnumerateWebAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        await AcquireWelcomePageAsync(parent, cancellationToken).ConfigureAwait(false);
        var endpoint = Endpoint(parent.WebUrl,
            $"_api/web/lists?$select={AllListsSelect}&$expand=RootFolder");
        var listResult = await ReadCollectionAsync(parent.ScopeKey, parent.ScopeKey,
            "lists:" + parent.ScopeKey, endpoint, AllListsSelect, string.Empty,
            AspxSurfaceApplicability.Applicable, "AllListsAuthority", cancellationToken).ConfigureAwait(false);
        var containers = new List<LiveScope>
        {
            Add(new LiveScope(Key("web-root", parent.WebUrl.AbsoluteUri), parent.ScopeKey,
                DiscoveryScopeKind.Container, null, parent.WebUrl.AbsoluteUri, "web-root", parent.WebUrl,
                null, null, null, null)),
        };
        foreach (var item in listResult.Items)
        {
            var listId = PropertyGuid(item, "Id");
            var baseType = PropertyInt(item, "BaseType");
            var template = PropertyInt(item, "BaseTemplate");
            var rootFolder = NestedString(item, "RootFolder", "ServerRelativeUrl");
            var title = PropertyString(item, "Title") ?? listId?.ToString("D") ?? "unknown-list";
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = title,
                ["baseType"] = baseType?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing",
                ["baseTemplate"] = template?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing",
                ["hidden"] = PropertyBool(item, "Hidden")?.ToString() ?? "missing",
                ["isCatalog"] = PropertyBool(item, "IsCatalog")?.ToString() ?? "missing",
                ["defaultViewUrl"] = PropertyString(item, "DefaultViewUrl") ?? string.Empty,
            };
            var scope = Add(new LiveScope(Key("list", parent.WebUrl.AbsoluteUri, listId?.ToString("D") ?? title),
                parent.ScopeKey, DiscoveryScopeKind.Container, null, rootFolder ?? title, "list", parent.WebUrl,
                listId, baseType, rootFolder, metadata));
            containers.Add(scope);
            RecordListApplicability(parent, scope, template);
        }
        return Result(parent, containers, listResult.Validation.Outcome);
    }

    private Task<DiscoveryChildEnumerationResult> EnumerateContainerAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parent.Role == "web-root")
        {
            var child = Add(new LiveScope(Key("folder", parent.WebUrl.AbsoluteUri, "web-root"), parent.ScopeKey,
                DiscoveryScopeKind.Folder, DiscoverySourceKind.WebRootFiles, parent.WebUrl.AbsolutePath,
                "folder", parent.WebUrl, null, null, parent.WebUrl.AbsolutePath, null));
            return Task.FromResult(Result(parent, new[] { child }, DiscoveryTerminalOutcome.Complete));
        }

        var children = new List<LiveScope>();
        var decision = AspxListApplicabilityPolicy.Evaluate(parent.BaseType);
        if (decision.RawLibraryRequired && !string.IsNullOrWhiteSpace(parent.FolderUrl))
            children.Add(Add(new LiveScope(Key("folder", parent.WebUrl.AbsoluteUri, parent.FolderUrl), parent.ScopeKey,
                DiscoveryScopeKind.Folder, DiscoverySourceKind.RawListLibraryFiles, parent.FolderUrl,
                "folder", parent.WebUrl, parent.ListId, parent.BaseType, parent.FolderUrl, parent.Metadata)));
        children.Add(Add(new LiveScope(Key("forms", parent.WebUrl.AbsoluteUri, parent.ListId?.ToString("D") ?? parent.ScopeKey),
            parent.ScopeKey, DiscoveryScopeKind.Folder, DiscoverySourceKind.ListFormBackingFiles,
            parent.Locator + "/Forms REST objects", "forms", parent.WebUrl, parent.ListId,
            parent.BaseType, parent.FolderUrl, parent.Metadata)));
        children.Add(Add(new LiveScope(Key("views", parent.WebUrl.AbsoluteUri, parent.ListId?.ToString("D") ?? parent.ScopeKey),
            parent.ScopeKey, DiscoveryScopeKind.Folder, DiscoverySourceKind.ListViewBackingFiles,
            parent.Locator + "/Views REST objects", "views", parent.WebUrl, parent.ListId,
            parent.BaseType, parent.FolderUrl, parent.Metadata)));

        var outcome = decision.Outcome;
        return Task.FromResult(Result(parent, children, outcome,
            outcome == DiscoveryTerminalOutcome.Unknown ? "invalid_or_unspecified_base_type" : null));
    }

    private async Task<DiscoveryChildEnumerationResult> EnumerateFolderAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        if (parent.Role is "forms" or "views")
            return Result(parent, Array.Empty<LiveScope>(), DiscoveryTerminalOutcome.Empty);
        var endpoint = FolderEndpoint(parent.WebUrl, parent.FolderUrl, "Folders", FoldersSelect);
        var result = await ReadCollectionAsync(parent.ScopeKey, parent.ScopeKey,
            "folders:" + parent.ScopeKey, endpoint, FoldersSelect, string.Empty,
            AspxSurfaceApplicability.Applicable, "RecursiveFolderAuthority", cancellationToken).ConfigureAwait(false);
        var children = result.Items.Select(item =>
        {
            var folderUrl = PropertyString(item, "ServerRelativeUrl");
            return Add(new LiveScope(Key("folder", parent.WebUrl.AbsoluteUri, folderUrl ?? Guid.NewGuid().ToString("N")),
                parent.ScopeKey, DiscoveryScopeKind.Folder, parent.SourceKind, folderUrl ?? "missing-folder-url",
                "folder", parent.WebUrl, parent.ListId, parent.BaseType, folderUrl, parent.Metadata));
        }).ToArray();
        return Result(parent, children, result.Validation.Outcome);
    }

    private async IAsyncEnumerable<RawDiscoveryBatch> ReadFolderFilesAsync(LiveScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var endpoint = FolderEndpoint(scope.WebUrl, scope.FolderUrl, "Files", FilesSelect);
        var result = await ReadCollectionAsync(scope.ScopeKey, scope.ParentScopeKey,
            "files:" + scope.ScopeKey, endpoint, FilesSelect, string.Empty,
            AspxSurfaceApplicability.Applicable, scope.SourceKind == DiscoverySourceKind.WebRootFiles
                ? "WebRootFilesAuthority" : "DocumentLibraryFilesAuthority", cancellationToken).ConfigureAwait(false);
        foreach (var page in result.Pages)
        {
            var records = page.Page.Items.Select(item => new RawDiscoveryRecord(
                PropertyString(item, "UniqueId"), PropertyString(item, "UniqueId"),
                scope.ListId?.ToString("D") ?? scope.ScopeKey, PropertyString(item, "Name"),
                PropertyString(item, "ServerRelativeUrl"), true, options.PermissionContext,
                EvidenceMetadata(endpoint, FilesSelect, string.Empty, page.Page.SchemaFlavor,
                    ("customizedPageStatus", PropertyString(item, "CustomizedPageStatus") ?? "unknown"))))
                .ToArray();
            yield return ToRawBatch(page, records, result.Validation.Outcome);
        }
    }

    private async IAsyncEnumerable<RawDiscoveryBatch> ReadReferencesAsync(LiveScope scope, bool isForm,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var select = isForm ? FormsSelect : ViewsSelect;
        var name = isForm ? "Forms" : "Views";
        var endpoint = Endpoint(scope.WebUrl,
            $"_api/web/lists(guid'{scope.ListId:D}')/{name}?$select={select}");
        var result = await ReadCollectionAsync(scope.ScopeKey, scope.ParentScopeKey,
            name.ToLowerInvariant() + ":" + scope.ScopeKey, endpoint, select, string.Empty,
            scope.BaseType == (int)ListBaseType.DocumentLibrary
                ? AspxSurfaceApplicability.Applicable : AspxSurfaceApplicability.SystemOrVirtualOnly,
            isForm ? "AllListFormsAuthority" : "AllListViewsAuthority", cancellationToken).ConfigureAwait(false);
        foreach (var page in result.Pages)
        {
            var records = new List<RawDiscoveryRecord>();
            foreach (var item in page.Page.Items)
            {
                var objectId = PropertyString(item, "Id") ?? DiscoveryHash.Of(item.GetRawText());
                var locator = PropertyString(item, "ServerRelativeUrl");
                var candidate = await ResolveReferenceAsync(scope, isForm, objectId, locator,
                    EvidenceRef(endpoint, page.Page, select), cancellationToken).ConfigureAwait(false);
                ReferenceCollector.AddReference(candidate.Candidate);
                if (candidate.Physical != null) records.Add(candidate.Physical);
            }
            yield return ToRawBatch(page, records, result.Validation.Outcome);
        }
    }

    private async Task<(AspxReferenceCandidate Candidate, RawDiscoveryRecord Physical)> ResolveReferenceAsync(
        LiveScope scope, bool isForm, string objectId, string locator, string evidenceRef,
        CancellationToken cancellationToken)
    {
        var sourceKind = isForm ? AspxReferenceSourceKinds.ListForm : AspxReferenceSourceKinds.ListView;
        var sourceObjectIdentity = scope.ListId == null ? objectId : scope.ListId.Value.ToString("D") + ":" + objectId;
        var method = isForm ? "SharePoint REST List.Forms + PnP.Core file resolution"
            : "SharePoint REST List.Views + PnP.Core file resolution";
        if (string.IsNullOrWhiteSpace(locator))
            return (Candidate(sourceKind, sourceObjectIdentity, method, locator, AspxReferenceDispositions.Unknown,
                "locator_missing", null, "unknown", evidenceRef), null);
        if (!string.Equals(Path.GetExtension(locator), ".aspx", StringComparison.OrdinalIgnoreCase))
            return (Candidate(sourceKind, sourceObjectIdentity, method, locator, AspxReferenceDispositions.NonAspx,
                "locator_not_aspx", null, "not-applicable", evidenceRef), null);

        var client = await clientFactory.GetAsync(scope.WebUrl, cancellationToken).ConfigureAwait(false);
        var resolved = await client.ResolveFileAsync(locator, cancellationToken).ConfigureAwait(false);
        if (resolved.Outcome != DiscoveryTerminalOutcome.Complete || string.IsNullOrWhiteSpace(resolved.FileUniqueId))
        {
            var disposition = resolved.Outcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed
                ? AspxReferenceDispositions.ReferenceUnavailable : AspxReferenceDispositions.Unknown;
            return (Candidate(sourceKind, sourceObjectIdentity, method, locator, disposition,
                resolved.ErrorCode, null, "unavailable", evidenceRef, resolved.EvidenceRef), null);
        }

        var ghosted = string.Equals(resolved.CustomizedPageStatus, nameof(CustomizedPageStatus.Uncustomized),
            StringComparison.OrdinalIgnoreCase);
        var dispositionValue = ghosted ? AspxReferenceDispositions.LinkedPhysicalGhosted
            : AspxReferenceDispositions.LinkedPhysicalCustomized;
        var contentOrigin = ghosted ? "verified-ghosted" : "verified-customized-or-physical";
        var physical = new RawDiscoveryRecord(objectId, resolved.FileUniqueId,
            scope.ListId?.ToString("D") ?? scope.ScopeKey, resolved.Name ?? Path.GetFileName(locator),
            resolved.ServerRelativeUrl ?? locator, true, options.PermissionContext,
            EvidenceMetadata(new Uri(scope.WebUrl, locator), string.Empty, string.Empty, "pnp-file-resolution",
                ("referenceSourceKind", sourceKind), ("referenceObjectId", objectId),
                ("customizedPageStatus", resolved.CustomizedPageStatus ?? "unknown")));
        return (Candidate(sourceKind, sourceObjectIdentity, method, locator, dispositionValue, null,
            resolved.FileUniqueId, contentOrigin, evidenceRef, resolved.EvidenceRef), physical);
    }

    private async Task AcquireWelcomePageAsync(LiveScope web, CancellationToken cancellationToken)
    {
        const string select = "WelcomePage";
        var endpoint = Endpoint(web.WebUrl, $"_api/web/RootFolder?$select={select}");
        var result = await ReadCollectionAsync(web.ScopeKey, web.ParentScopeKey,
            "welcome-page:" + web.ScopeKey, endpoint, select, string.Empty,
            AspxSurfaceApplicability.SystemOrVirtualOnly, "WebWelcomePageAuthority", cancellationToken)
            .ConfigureAwait(false);
        if (result.Validation.Outcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed)
        {
            ReferenceCollector.AddReference(Candidate(AspxReferenceSourceKinds.WebWelcomePage, web.ScopeKey,
                "SharePoint REST Web.RootFolder.WelcomePage", null,
                AspxReferenceDispositions.ReferenceUnavailable,
                result.Validation.Outcome == DiscoveryTerminalOutcome.Denied ? "welcome_page_denied" : "welcome_page_failed",
                null, "unavailable", EvidenceRef(endpoint, result.Pages.FirstOrDefault()?.Page, select)));
            return;
        }
        var value = result.Items.Select(item => PropertyString(item, "WelcomePage")).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(value))
        {
            ReferenceCollector.AddReference(Candidate(AspxReferenceSourceKinds.WebWelcomePage, web.ScopeKey,
                "SharePoint REST Web.RootFolder.WelcomePage", value, AspxReferenceDispositions.NonAspx,
                "welcome_page_success_empty", null, "empty", EvidenceRef(endpoint, result.Pages.FirstOrDefault()?.Page, select)));
            return;
        }
        var absolutePath = value.StartsWith('/') ? value : web.WebUrl.AbsolutePath.TrimEnd('/') + "/" + value.TrimStart('/');
        var resolved = await ResolveReferenceAsync(web with { ListId = null }, isForm: false,
            web.ScopeKey, absolutePath, EvidenceRef(endpoint, result.Pages.FirstOrDefault()?.Page, select),
            cancellationToken).ConfigureAwait(false);
        ReferenceCollector.AddReference(resolved.Candidate with
        {
            SourceKind = AspxReferenceSourceKinds.WebWelcomePage,
            AcquisitionMethod = "SharePoint REST Web.RootFolder.WelcomePage + PnP.Core file resolution",
        });
    }

    private void RecordListApplicability(LiveScope web, LiveScope list, int? template)
    {
        var decision = AspxListApplicabilityPolicy.Evaluate(list.BaseType);
        var applicability = decision.Applicability;
        var counterexample = decision.RuntimeCounterexampleState;
        var outcome = decision.Outcome;
        var evidence = new[]
        {
            $"actual-BaseType={list.BaseType?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}",
            $"BaseTemplate={template?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}",
            "BaseTemplate/Hidden/IsCatalog retained as provenance only",
        };
        ReferenceCollector.AddSurface(SurfaceRow(web.ScopeKey, web.ScopeKey,
            "list-applicability:" + list.ScopeKey, list.Locator, string.Empty, string.Empty,
            "ActualListBaseTypeAuthority", applicability, outcome, 1, AspxExpectedCountState.Known,
            1, DiscoveryHash.Of("list-applicability", list.ScopeKey), 0, evidence,
            applicability == AspxSurfaceApplicability.Applicable ? "raw-files+physical-forms+forms+views"
                : "forms+views"), Array.Empty<AspxPaginationPageReceipt>(),
            outcome == DiscoveryTerminalOutcome.Unknown
                ? new[] { counterexample == AspxRuntimeCounterexampleState.Observed
                    ? "not_applicable_runtime_counterexample" : "invalid_or_unspecified_base_type" }
                : Array.Empty<string>());
    }

    private async Task<LiveCollectionResult> ReadCollectionAsync(string scopeKey, string parentScopeKey,
        string surfaceId, Uri initialEndpoint, string select, string filter,
        AspxSurfaceApplicability applicability, string adapter, CancellationToken cancellationToken)
    {
        var client = await clientFactory.GetAsync(WebUrlFromEndpoint(initialEndpoint), cancellationToken)
            .ConfigureAwait(false);
        var pages = new List<LivePage>();
        var items = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Uri next = initialEndpoint;
        string requestToken = null;
        var providerOutcome = DiscoveryTerminalOutcome.Complete;
        for (var ordinal = 0; next != null; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SharePointRestPage page;
            try
            {
                page = await client.GetPageAsync(next, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                page = new SharePointRestPage(next, 0, Array.Empty<JsonElement>(), null,
                    DiscoveryHash.Of(ex.GetType().FullName, ex.Message), "transport-error",
                    DiscoveryTerminalOutcome.Failed, "transport_failure");
            }
            var nextLink = page.NextLink;
            var terminal = string.IsNullOrWhiteSpace(nextLink) || page.Outcome != DiscoveryTerminalOutcome.Complete;
            var nextUri = terminal ? null : ResolveNext(initialEndpoint, nextLink);
            var receipt = new AspxPaginationPageReceipt(surfaceId, options.AuthorityRevision,
                DiscoveryHash.Of(initialEndpoint.GetLeftPart(UriPartial.Path), select, filter ?? string.Empty),
                ordinal, AspxPaginationContract.TokenHash(requestToken), page.Items.Count,
                AspxPaginationContract.TokenHash(nextLink), page.ResponseDigest, terminal, DateTimeOffset.UtcNow);
            pages.Add(new LivePage(page, receipt));
            items.AddRange(page.Items);
            if (page.Outcome != DiscoveryTerminalOutcome.Complete)
            {
                providerOutcome = page.Outcome;
                next = null;
            }
            else if (nextUri == null)
            {
                next = null;
            }
            else if (!seen.Add(nextLink))
            {
                providerOutcome = DiscoveryTerminalOutcome.Unknown;
                ReferenceCollector.AddGap(surfaceId + ":pagination_token_loop_or_loss");
                next = null;
            }
            else
            {
                requestToken = nextLink;
                next = nextUri;
            }
        }

        var validation = AspxPaginationContract.Validate(pages.Select(page => page.Receipt).ToArray(), providerOutcome);
        var expectedState = validation.Outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty
            ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown;
        int? expected = expectedState == AspxExpectedCountState.Known ? items.Count : null;
        var outcome = validation.Outcome == DiscoveryTerminalOutcome.Complete && items.Count == 0
            ? DiscoveryTerminalOutcome.Empty : validation.Outcome;
        var absenceKind = outcome == DiscoveryTerminalOutcome.Empty ? "AuthorityTerminalZero" : null;
        var row = SurfaceRow(scopeKey, parentScopeKey, surfaceId, initialEndpoint.AbsoluteUri, select,
            filter ?? string.Empty, "SharePointRestCollectionAuthority", applicability, outcome,
            expected, expectedState, items.Count, validation.ChainHash, validation.OutstandingTokenCount,
            pages.Select(page => EvidenceRef(initialEndpoint, page.Page, select)).ToArray(), adapter,
            absenceKind, absenceKind == null ? null : initialEndpoint.AbsoluteUri);
        ReferenceCollector.AddSurface(row, pages.Select(page => page.Receipt).ToArray(),
            validation.GapCodes.Select(code => surfaceId + ":" + code).ToArray());
        return new(items, pages, validation with { Outcome = outcome });
    }

    private AspxSurfaceDenominatorRow SurfaceRow(string scopeKey, string parentScopeKey, string surfaceId,
        string endpoint, string select, string filter, string authorityKind,
        AspxSurfaceApplicability applicability, DiscoveryTerminalOutcome outcome, int? expected,
        AspxExpectedCountState expectedState, int observed, string chainHash, int outstanding,
        IReadOnlyList<string> evidenceRefs, string adapter, string absenceKind = null, string absenceRef = null) =>
        new(AspxAcquisitionVersions.SurfaceContract, Guid.Empty, null, null, scopeKey, parentScopeKey,
            surfaceId, applicability, null, null, null, null, null, null,
            applicability == AspxSurfaceApplicability.NotApplicable
                ? AspxRuntimeCounterexampleState.Observed : AspxRuntimeCounterexampleState.NoneObserved,
            authorityKind, endpoint, options.AuthorityRevision, options.AuthorityHash,
            "GET", endpoint, select, filter, options.VisibilityBoundary, options.PermissionContext,
            expected, expectedState, observed, outcome,
            outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty ? "satisfied"
                : outcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or
                    DiscoveryTerminalOutcome.Truncated or DiscoveryTerminalOutcome.Cancelled ? "incomplete" : "unknown",
            outstanding != 0, chainHash, outstanding, absenceKind, absenceRef,
            null, null, null, null, null, null, null, DateTimeOffset.UtcNow,
            evidenceRefs ?? Array.Empty<string>(), adapter,
            applicability == AspxSurfaceApplicability.Applicable ? "physical-and-reference"
                : applicability == AspxSurfaceApplicability.SystemOrVirtualOnly ? "reference-first" : "fail-closed");

    private static RawDiscoveryBatch ToRawBatch(LivePage page, IReadOnlyList<RawDiscoveryRecord> records,
        DiscoveryTerminalOutcome finalOutcome)
    {
        var terminal = page.Receipt.TerminalFlag;
        var outcome = terminal ? finalOutcome : DiscoveryTerminalOutcome.Pending;
        return new(page.Receipt.PageOrdinal,
            DiscoveryHash.Of(page.Receipt.ActualEndpointHash, page.Receipt.RequestTokenHash),
            page.Receipt.ResponseDigest, records, terminal, outcome, page.Receipt.NextTokenHash,
            outcome == DiscoveryTerminalOutcome.Unknown ? DiscoveryGapCodes.PaginationTokenLoopOrLoss : null,
            outcome == DiscoveryTerminalOutcome.Unknown ? "Live pagination receipt failed v3 integrity validation." : null);
    }

    private AspxReferenceCandidate Candidate(string sourceKind, string objectId, string method, string locator,
        string disposition, string reason, string fileUniqueId, string contentOrigin,
        params string[] evidence) => new(sourceKind, objectId, method, null, locator,
            AspxPlatformRegistryV1.NormalizeRequestPath(locator), null, disposition, reason, fileUniqueId,
            contentOrigin, options.PermissionContext,
            evidence.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());

    private static Dictionary<string, string> EvidenceMetadata(Uri endpoint, string select, string filter,
        string schemaFlavor, params (string Key, string Value)[] additional)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["actualMethod"] = "GET",
            ["actualEndpoint"] = endpoint.AbsoluteUri,
            ["actualSelect"] = select ?? string.Empty,
            ["actualFilter"] = filter ?? string.Empty,
            ["responseSchemaFlavor"] = schemaFlavor ?? "unknown",
        };
        foreach (var (key, value) in additional) metadata[key] = value ?? string.Empty;
        return metadata;
    }

    private string EvidenceRef(Uri endpoint, SharePointRestPage page, string select) =>
        page == null ? $"GET {endpoint.AbsoluteUri};$select={select};no-response"
            : $"GET {endpoint.AbsoluteUri};$select={select};$filter=;status={(int)page.StatusCode};schema={page.SchemaFlavor};digest={page.ResponseDigest}";

    private static DiscoveryChildEnumerationResult Result(LiveScope parent,
        IReadOnlyList<LiveScope> children, DiscoveryTerminalOutcome outcome, string gapCode = null)
    {
        var expected = children.Select(child => new DiscoveryChildExpectation(child.ScopeKey, child.Kind,
            child.SourceKind, child.Locator, child.PermissionContext, Required: true)).ToArray();
        return new(AspxDiscoveryOrchestrator.ChildKindFor(parent.Kind), expected,
            children.Select(Registration).ToArray(), outcome, parent.PermissionContext, gapCode,
            gapCode == null ? null : $"Live acquisition retained '{parent.ScopeKey}' and failed closed: {gapCode}.");
    }

    private static DiscoveryScopeRegistration Registration(LiveScope scope) => new(
        scope.ScopeKey, scope.ParentScopeKey, scope.Kind, scope.SourceKind, scope.Locator,
        scope.PermissionContext, Required: true);

    private LiveScope Add(LiveScope scope)
    {
        scopes.TryAdd(scope.ScopeKey, scope);
        return scopes[scope.ScopeKey];
    }

    private string Key(params string[] values) => DiscoveryHash.Of(values)[..24];
    private static Uri Endpoint(Uri webUrl, string relative) => new(webUrl.AbsoluteUri.TrimEnd('/') + "/" + relative.TrimStart('/'));
    private static Uri FolderEndpoint(Uri webUrl, string folderUrl, string child, string select)
    {
        var escaped = (folderUrl ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
        return Endpoint(webUrl, $"_api/web/GetFolderByServerRelativePath(decodedurl='{escaped}')/{child}?$select={select}");
    }
    private static Uri ResolveNext(Uri initial, string next) =>
        Uri.TryCreate(next, UriKind.Absolute, out var absolute) ? absolute : new Uri(initial, next);
    private static Uri WebUrlFromEndpoint(Uri endpoint)
    {
        var marker = endpoint.AbsoluteUri.IndexOf("/_api/", StringComparison.OrdinalIgnoreCase);
        return marker > 0 ? new Uri(endpoint.AbsoluteUri[..marker]) : new Uri(endpoint.GetLeftPart(UriPartial.Authority));
    }
    private static string PropertyString(JsonElement item, string name) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, name, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value.ToString() : null;
    private static int? PropertyInt(JsonElement item, string name) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, name, out var value) &&
        (value.TryGetInt32(out var number) || int.TryParse(value.ToString(), out number)) ? number : null;
    private static bool? PropertyBool(JsonElement item, string name) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, name, out var value) &&
        (value.ValueKind is JsonValueKind.True or JsonValueKind.False || bool.TryParse(value.ToString(), out _))
            ? value.ValueKind == JsonValueKind.True || bool.Parse(value.ToString()) : null;
    private static Guid? PropertyGuid(JsonElement item, string name) =>
        Guid.TryParse(PropertyString(item, name), out var value) ? value : null;
    private static string NestedString(JsonElement item, string parent, string child) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, parent, out var nested) ? PropertyString(nested, child) : null;
    private static DiscoveryTerminalOutcome MergeOutcome(DiscoveryTerminalOutcome current,
        DiscoveryTerminalOutcome next)
    {
        if (current == DiscoveryTerminalOutcome.Unknown || next == DiscoveryTerminalOutcome.Unknown)
            return DiscoveryTerminalOutcome.Unknown;
        if (current is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or DiscoveryTerminalOutcome.Truncated ||
            next is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or DiscoveryTerminalOutcome.Truncated)
            return next is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or DiscoveryTerminalOutcome.Truncated
                ? next : current;
        return current;
    }

    private sealed record LiveScope(
        string ScopeKey,
        string ParentScopeKey,
        DiscoveryScopeKind Kind,
        DiscoverySourceKind? SourceKind,
        string Locator,
        string Role,
        Uri WebUrl,
        Guid? ListId,
        int? BaseType,
        string FolderUrl,
        IReadOnlyDictionary<string, string> Metadata)
    {
        internal string PermissionContext => "live:" + Role;
    }

    private sealed record LivePage(SharePointRestPage Page, AspxPaginationPageReceipt Receipt);
    private sealed record LiveCollectionResult(
        IReadOnlyList<JsonElement> Items,
        IReadOnlyList<LivePage> Pages,
        AspxPaginationValidation Validation);
}

internal sealed class LiveRawDiscoverySource : IRawDiscoverySource
{
    private readonly Func<CancellationToken, IAsyncEnumerable<RawDiscoveryBatch>> factory;
    internal LiveRawDiscoverySource(Func<CancellationToken, IAsyncEnumerable<RawDiscoveryBatch>> factory) =>
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync(CancellationToken cancellationToken = default) =>
        factory(cancellationToken);
}
