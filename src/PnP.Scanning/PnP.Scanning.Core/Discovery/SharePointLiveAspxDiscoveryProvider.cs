using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal sealed record SharePointLiveAspxDiscoveryOptions(
    IReadOnlyList<Uri> SiteCollectionUrls,
    string PermissionContext,
    string VisibilityBoundary,
    string AuthorityRevision,
    string AuthorityHash,
    string PlatformBuildRef = null,
    AspxTenantAuthoritySnapshot AuthoritySnapshot = null);

internal sealed record SharePointRestPage(
    Uri RequestUri,
    HttpStatusCode StatusCode,
    IReadOnlyList<JsonElement> Items,
    string NextLink,
    string ResponseDigest,
    string SchemaFlavor,
    DiscoveryTerminalOutcome Outcome,
    string ErrorCode = null,
    string SemanticDetectorResult = SharePointSemanticDetectorResults.None,
    DateTimeOffset ReceivedAtUtc = default,
    int AttemptCount = 1,
    int AttemptLimit = 1,
    string RequestId = null,
    string CorrelationId = null);

internal static class SharePointSemanticDetectorResults
{
    internal const string None = "none";
    internal const string LoginShell = "login-shell";
    internal const string AccessDenied = "access-denied";
    internal const string Unauthorized = "unauthorized";
    internal const string ErrorEnvelope = "error-envelope";

    internal static bool IsKnown(string value) => value is None or LoginShell or AccessDenied or
        Unauthorized or ErrorEnvelope;
}

internal sealed record SharePointSemanticDetection(string Result, bool IsDenied, string ErrorCode);

internal static class SharePointSemanticDenialDetector
{
    private const int InspectionLimit = 131072;

    internal static SharePointSemanticDetection Detect(byte[] body, string mediaType = null)
    {
        if (body == null || body.Length == 0)
            return new(SharePointSemanticDetectorResults.None, false, null);
        var inspected = body.AsSpan(0, Math.Min(body.Length, InspectionLimit));
        var text = Encoding.UTF8.GetString(inspected);
        var trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        var htmlLike = (mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? false) ||
            trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        if (htmlLike)
        {
            if (ContainsAny(trimmed, "login.microsoftonline.com", "wa=wsignin1.0", "Sign in to your account",
                    "id=\"loginForm\"", "name=\"loginfmt\""))
                return new(SharePointSemanticDetectorResults.LoginShell, true, "semantic_login_shell");
            if (ContainsAny(trimmed, "Access Denied", "AccessDenied.aspx", "Sorry, you don't have access",
                    "You need permission to access this site"))
                return new(SharePointSemanticDetectorResults.AccessDenied, true, "semantic_access_denied");
            if (ContainsAny(trimmed, "401 Unauthorized", ">Unauthorized<"))
                return new(SharePointSemanticDetectorResults.Unauthorized, true, "semantic_unauthorized");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (TryErrorEnvelope(document.RootElement, out var errorText, out var serverErrorCode))
            {
                if (ContainsAny(errorText, "access denied", "accessdenied", "does not have permissions",
                        "-2147024891"))
                    return new(SharePointSemanticDetectorResults.AccessDenied, true,
                        serverErrorCode ?? "semantic_access_denied");
                if (ContainsAny(errorText, "unauthorized", "unauthenticated", "401"))
                    return new(SharePointSemanticDetectorResults.Unauthorized, true,
                        serverErrorCode ?? "semantic_unauthorized");
                return new(SharePointSemanticDetectorResults.ErrorEnvelope, false,
                    serverErrorCode ?? "semantic_error_envelope");
            }
        }
        catch (JsonException)
        {
            // Non-JSON success bodies are handled by the normal response parser after semantic shell detection.
        }

        if (!htmlLike && trimmed.Length < 4096)
        {
            if (trimmed.StartsWith("Access denied", StringComparison.OrdinalIgnoreCase))
                return new(SharePointSemanticDetectorResults.AccessDenied, true, "semantic_access_denied");
            if (trimmed.StartsWith("Unauthorized", StringComparison.OrdinalIgnoreCase))
                return new(SharePointSemanticDetectorResults.Unauthorized, true, "semantic_unauthorized");
        }
        return new(SharePointSemanticDetectorResults.None, false, null);
    }

    private static bool TryErrorEnvelope(JsonElement root, out string errorText, out string errorCode)
    {
        foreach (var name in new[] { "error", "odata.error" })
            if (PnPContextSharePointAspxRestClient.TryProperty(root, name, out var error))
            {
                errorText = error.GetRawText();
                errorCode = PnPContextSharePointAspxRestClient.String(error, "code");
                return true;
            }
        if (PnPContextSharePointAspxRestClient.TryProperty(root, "d", out var verbose) &&
            PnPContextSharePointAspxRestClient.TryProperty(verbose, "error", out var verboseError))
        {
            errorText = verboseError.GetRawText();
            errorCode = PnPContextSharePointAspxRestClient.String(verboseError, "code");
            return true;
        }
        errorText = null;
        errorCode = null;
        return false;
    }

    private static bool ContainsAny(string value, params string[] markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}

internal static class SharePointRestResponseParser
{
    internal static SharePointRestPage Parse(Uri requestUri, HttpStatusCode statusCode, byte[] bytes,
        string mediaType = null, string requestId = null, string correlationId = null,
        DateTimeOffset? receivedAtUtc = null, int attemptCount = 1, int attemptLimit = 1)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        bytes ??= Array.Empty<byte>();
        var received = receivedAtUtc ?? DateTimeOffset.UtcNow;
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var semantic = SharePointSemanticDenialDetector.Detect(bytes, mediaType);
        var transportOutcome = statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? DiscoveryTerminalOutcome.Denied
            : (int)statusCode >= 200 && (int)statusCode <= 299
                ? DiscoveryTerminalOutcome.Complete : DiscoveryTerminalOutcome.Failed;
        if (transportOutcome != DiscoveryTerminalOutcome.Complete)
            return new(requestUri, statusCode, Array.Empty<JsonElement>(), null, digest, "http-error",
                transportOutcome, semantic.ErrorCode ?? "http_" + (int)statusCode, semantic.Result,
                received, attemptCount, attemptLimit, requestId, correlationId);
        if (semantic.IsDenied)
            return new(requestUri, statusCode, Array.Empty<JsonElement>(), null, digest,
                "semantic-denial-" + semantic.Result, DiscoveryTerminalOutcome.Denied,
                semantic.ErrorCode, semantic.Result, received, attemptCount, attemptLimit, requestId, correlationId);
        if (semantic.Result == SharePointSemanticDetectorResults.ErrorEnvelope)
            return new(requestUri, statusCode, Array.Empty<JsonElement>(), null, digest,
                "semantic-error-envelope", DiscoveryTerminalOutcome.Failed, semantic.ErrorCode,
                semantic.Result, received, attemptCount, attemptLimit, requestId, correlationId);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var parsed = PnPContextSharePointAspxRestClient.ParseEnvelope(document.RootElement);
            return new(requestUri, statusCode, parsed.Items, parsed.NextLink, digest,
                parsed.SchemaFlavor, DiscoveryTerminalOutcome.Complete, null, semantic.Result,
                received, attemptCount, attemptLimit, requestId, correlationId);
        }
        catch (JsonException)
        {
            return new(requestUri, statusCode, Array.Empty<JsonElement>(), null, digest,
                "invalid-json", DiscoveryTerminalOutcome.Failed, "response_json_invalid", semantic.Result,
                received, attemptCount, attemptLimit, requestId, correlationId);
        }
    }
}

internal static class AspxDurableRequestEvidence
{
    private const string HashedContinuationMarker = "sha256";

    internal static string Endpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrEmpty(endpoint.Query)) return endpoint.AbsoluteUri;
        var query = endpoint.Query[1..].Split('&', StringSplitOptions.None)
            .Select(SanitizeQuerySegment);
        return endpoint.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', query) + endpoint.Fragment;
    }

    internal static bool ContainsRawContinuationValue(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Query))
            return false;
        foreach (var segment in parsed.Query[1..].Split('&', StringSplitOptions.None))
        {
            var separator = segment.IndexOf('=');
            var key = separator < 0 ? segment : segment[..separator];
            if (!IsContinuationKey(key) || separator < 0) continue;
            var value = segment[(separator + 1)..];
            if (!string.IsNullOrEmpty(value) &&
                !string.Equals(value, HashedContinuationMarker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static string Reference(Uri endpoint, SharePointRestPage page, string select)
    {
        if (page == null) return $"GET {Endpoint(endpoint)};$select={select};no-response";
        var received = page.ReceivedAtUtc == default ? DateTimeOffset.UtcNow : page.ReceivedAtUtc;
        return $"GET {Endpoint(page.RequestUri)};$select={select};$filter=;status={(int)page.StatusCode};semantic={page.SemanticDetectorResult};attempt={page.AttemptCount}/{page.AttemptLimit};receivedUtc={received.ToUniversalTime():O};requestId={page.RequestId ?? "unavailable"};correlationId={page.CorrelationId ?? "unavailable"};errorCode={page.ErrorCode ?? "none"};schema={page.SchemaFlavor};digest={page.ResponseDigest}";
    }

    private static string SanitizeQuerySegment(string segment)
    {
        var separator = segment.IndexOf('=');
        var key = separator < 0 ? segment : segment[..separator];
        return IsContinuationKey(key) && separator >= 0 ? key + "=" + HashedContinuationMarker : segment;
    }

    private static bool IsContinuationKey(string encodedKey)
    {
        string key;
        try
        {
            key = Uri.UnescapeDataString(encodedKey.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            key = encodedKey;
        }
        var normalized = key.Trim().TrimStart('$').Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "skiptoken" or "deltatoken" or "continuationtoken" or "nexttoken" or
            "pagetoken" or "pagingtoken" or "paginginfo";
    }
}

internal sealed record SharePointResolvedFile(
    DiscoveryTerminalOutcome Outcome,
    string FileUniqueId,
    string Name,
    string ServerRelativeUrl,
    string CustomizedPageStatus,
    string EvidenceRef,
    string ErrorCode = null,
    int? ListItemId = null,
    string ContentTypeId = null);

internal sealed record SharePointModeledValue(
    DiscoveryTerminalOutcome Outcome,
    string Value,
    string Provider,
    string Operation,
    string ErrorCode,
    string EvidenceRef,
    DateTimeOffset ReceivedAtUtc);

internal sealed record SharePointModeledFolder(
    string UniqueId,
    string Name,
    string ServerRelativeUrl);

internal sealed record SharePointModeledFile(
    string UniqueId,
    string Name,
    string ServerRelativeUrl,
    string CustomizedPageStatus);

internal sealed record SharePointModeledFolderResult(
    DiscoveryTerminalOutcome Outcome,
    string FolderUniqueId,
    string FolderServerRelativeUrl,
    IReadOnlyList<SharePointModeledFolder> Folders,
    IReadOnlyList<SharePointModeledFile> Files,
    string Provider,
    string Operation,
    string ErrorCode,
    string EvidenceRef,
    DateTimeOffset ReceivedAtUtc);

internal interface ISharePointAspxRestClient : IDisposable
{
    Uri WebUrl { get; }
    Task<SharePointRestPage> GetPageAsync(Uri requestUri, CancellationToken cancellationToken = default);
    Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl,
        CancellationToken cancellationToken = default);
    Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken cancellationToken = default);
    Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl,
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
    internal const string AcquisitionUserAgent = "testtraffic-smr";

    private readonly PnPContext context;

    internal PnPContextSharePointAspxRestClient(PnPContext context) =>
        this.context = context ?? throw new ArgumentNullException(nameof(context));

    public Uri WebUrl => context.Uri;

    public async Task<SharePointRestPage> GetPageAsync(Uri requestUri,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateGetRequest(requestUri);
        await context.AuthenticationProvider.AuthenticateRequestAsync(context.Uri, request).ConfigureAwait(false);
        using var response = await context.RestClient.Client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return SharePointRestResponseParser.Parse(requestUri, response.StatusCode, bytes,
            response.Content.Headers.ContentType?.MediaType, Header(response, "SPRequestGuid", "request-id", "x-ms-request-id"),
            Header(response, "x-ms-correlation-id", "x-ms-correlation-request-id", "client-request-id"),
            DateTimeOffset.UtcNow, attemptCount: 1, attemptLimit: 1);
    }

    internal static HttpRequestMessage CreateGetRequest(Uri requestUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json;odata=nometadata");
        request.Headers.UserAgent.ParseAdd(AcquisitionUserAgent);
        return request;
    }

    public async Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await context.Web.GetFileByServerRelativeUrlOrDefaultAsync(serverRelativeUrl,
                item => item.UniqueId, item => item.Name, item => item.ServerRelativeUrl,
                item => item.CustomizedPageStatus,
                item => item.ListItemAllFields)
                .ConfigureAwait(false);
            if (file == null)
                return new(DiscoveryTerminalOutcome.Failed, null, null, serverRelativeUrl, null,
                    "PnP.Core:IWeb.GetFileByServerRelativeUrlOrDefaultAsync", "locator_not_found");
            return new(DiscoveryTerminalOutcome.Complete, file.UniqueId.ToString("D"), file.Name,
                file.ServerRelativeUrl, file.CustomizedPageStatus.ToString(),
                "PnP.Core:IWeb.GetFileByServerRelativeUrlOrDefaultAsync", null,
                file.ListItemAllFields?.Id, null);
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

    public async Task<SharePointModeledValue> ReadWelcomePageAsync(
        CancellationToken cancellationToken = default)
    {
        const string provider = "PnP.Core.Model.SharePoint:IWeb";
        const string operation = "GetAsync(WelcomePage)";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var web = await context.Web.GetAsync(item => item.WelcomePage).ConfigureAwait(false);
            return new(DiscoveryTerminalOutcome.Complete, web.WelcomePage, provider, operation, null,
                provider + ":" + operation, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var denied = IsDenied(ex);
            return new(denied ? DiscoveryTerminalOutcome.Denied : DiscoveryTerminalOutcome.Failed,
                null, provider, operation, denied ? "welcome_page_denied" : "welcome_page_failed",
                provider + ":" + operation + ":" + ex.GetType().Name, DateTimeOffset.UtcNow);
        }
    }

    public async Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl,
        CancellationToken cancellationToken = default)
    {
        const string provider = "PnP.Core.Model.SharePoint:IFolder";
        const string operation = "GetFolderByServerRelativeUrlOrDefaultAsync(Folders,Files)";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = await context.Web.GetFolderByServerRelativeUrlAsync(serverRelativeUrl,
                item => item.UniqueId, item => item.ServerRelativeUrl,
                item => item.Folders.QueryProperties(child => child.UniqueId, child => child.Name,
                    child => child.ServerRelativeUrl),
                item => item.Files.QueryProperties(file => file.UniqueId, file => file.Name,
                    file => file.ServerRelativeUrl, file => file.CustomizedPageStatus)).ConfigureAwait(false);
            if (folder == null)
                return new(DiscoveryTerminalOutcome.Failed, null, serverRelativeUrl,
                    Array.Empty<SharePointModeledFolder>(), Array.Empty<SharePointModeledFile>(),
                    provider, operation, "folder_not_found", provider + ":" + operation,
                    DateTimeOffset.UtcNow);
            var folders = folder.Folders.Select(child => new SharePointModeledFolder(
                child.UniqueId.ToString("D"), child.Name, child.ServerRelativeUrl)).ToArray();
            var files = folder.Files.Select(file => new SharePointModeledFile(
                file.UniqueId.ToString("D"), file.Name, file.ServerRelativeUrl,
                file.CustomizedPageStatus.ToString())).ToArray();
            var outcome = folders.Length == 0 && files.Length == 0
                ? DiscoveryTerminalOutcome.Empty : DiscoveryTerminalOutcome.Complete;
            return new(outcome, folder.UniqueId.ToString("D"), folder.ServerRelativeUrl, folders, files,
                provider, operation, null, provider + ":" + operation, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var denied = IsDenied(ex);
            return new(denied ? DiscoveryTerminalOutcome.Denied : DiscoveryTerminalOutcome.Failed,
                null, serverRelativeUrl, Array.Empty<SharePointModeledFolder>(),
                Array.Empty<SharePointModeledFile>(), provider, operation,
                denied ? "web_root_folder_denied" : "web_root_folder_failed",
                provider + ":" + operation + ":" + ex.GetType().Name, DateTimeOffset.UtcNow);
        }
    }

    public void Dispose() => context.Dispose();

    private static bool IsDenied(Exception ex) =>
        ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);

    internal static (IReadOnlyList<JsonElement> Items, string NextLink, string SchemaFlavor) ParseEnvelope(
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

    private static string Header(HttpResponseMessage response, params string[] names)
    {
        foreach (var name in names)
        {
            if (response.Headers.TryGetValues(name, out var values))
                return BoundedHeader(values.FirstOrDefault());
            if (response.Content.Headers.TryGetValues(name, out values))
                return BoundedHeader(values.FirstOrDefault());
        }
        return null;
    }

    private static string BoundedHeader(string value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, 256)];
    }
}

internal sealed record AspxSurfaceDispositionRule(
    string RuleId,
    string RuleVersion,
    string RuleHash,
    string ReviewRef,
    string PlatformBinding,
    int TriggerStatusCode,
    string TriggerErrorCode,
    string ReasonCode,
    string ClassificationEffect,
    IReadOnlyList<string> EvidenceRefs)
{
    internal bool Applies(SharePointRestPage page) => page != null &&
        (int)page.StatusCode == TriggerStatusCode &&
        (string.IsNullOrWhiteSpace(TriggerErrorCode) ||
            string.Equals(page.ErrorCode, TriggerErrorCode, StringComparison.Ordinal));
}

internal static class AspxSystemListFormsPolicy
{
    internal const int UserInformationListTemplate = 112;
    internal const string RuleId = "sharepoint-user-information-list-forms-http-400";
    internal const string RuleVersion = "v1";
    internal const string ReasonCode = "system_list_forms_http_400_reference_unknown";

    internal static AspxSurfaceDispositionRule Create(IReadOnlyDictionary<string, string> metadata,
        string rootFolder, string platformBuildRef)
    {
        if (metadata == null || !metadata.TryGetValue("baseTemplate", out var templateValue) ||
            !int.TryParse(templateValue, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var template) ||
            template != UserInformationListTemplate || string.IsNullOrWhiteSpace(rootFolder) ||
            !rootFolder.TrimEnd('/').EndsWith("/_catalogs/users", StringComparison.OrdinalIgnoreCase))
            return null;
        var platform = string.IsNullOrWhiteSpace(platformBuildRef) ? "unbound-build" : platformBuildRef;
        var material = string.Join('|', RuleId, RuleVersion, UserInformationListTemplate,
            "/_catalogs/users", 400, "http_400", platform,
            "SystemOrVirtualOnly", "Failed", "expectedCount=Unknown", "historical/system/virtual applicability explicit");
        return new(RuleId, RuleVersion, DiscoveryHash.Of(material), "CCD-726+CCD-734@2026-09-12",
            platform, 400, null, ReasonCode,
            "system-or-virtual-applicability-explicit;http-400-failed;historical-virtual-unknown",
            new[]
            {
                "sealed-run:62dbcc5b-80fd-4055-b528-745f9451ef2a",
                "kb:dev.titao@4c91e3a3e0544d871c7faad9f5d029b7ce55e035",
                $"platform-build:{platform}",
            });
    }
}

/// <summary>
/// Authentication/context-backed live SharePoint acquisition provider. All authority collections are
/// requested without BaseType, Hidden, catalog, template or page-family admission filters.
/// </summary>
internal sealed class SharePointLiveAspxDiscoveryProvider : IAspxDiscoveryProvider, IAspxReferenceAcquisitionProvider
{
    private const string AllListsSelect = "Id,Title,BaseType,BaseTemplate,Hidden,IsCatalog,RootFolder/ServerRelativeUrl,RootFolder/UniqueId,DefaultViewUrl";
    private const string FormsSelect = "Id,ServerRelativeUrl,FormType";
    private const string ViewsSelect = "Id,ServerRelativeUrl,Hidden,DefaultView,PersonalView,Title";
    private const string FilesSelect = "UniqueId,Name,ServerRelativeUrl,CustomizedPageStatus,ListItemAllFields/Id,ListItemAllFields/ContentTypeId,Length";
    private const string FoldersSelect = "UniqueId,Name,ServerRelativeUrl";
    private const string WebsSelect = "Id,Url,ServerRelativeUrl,Title,WebTemplate,Configuration";
    private readonly SharePointLiveAspxDiscoveryOptions options;
    private readonly AspxTenantAuthoritySnapshot authority;
    private readonly ISharePointAspxRestClientFactory clientFactory;
    private readonly Dictionary<string, LiveScope> scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharePointModeledFolderResult> modeledFolders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> welcomePageByWeb = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    internal SharePointLiveAspxDiscoveryProvider(SharePointLiveAspxDiscoveryOptions options,
        ISharePointAspxRestClientFactory clientFactory)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        if (options.SiteCollectionUrls?.Any(url => url == null || !url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps) == true)
            throw new ArgumentException("Every site collection URL must be an absolute HTTPS URL.", nameof(options));
        authority = options.AuthoritySnapshot ?? LegacyDeclaredAuthority(options);
        var root = new LiveScope("tenant", null, DiscoveryScopeKind.Tenant, null,
            "sharepoint-live://" + authority.ScopeMode, "tenant", null, null, null, null, null);
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
            "folder" or "physical-forms" or "web-root-folder" => ReadFolderFilesAsync(scope, token),
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
        var geo = Add(new LiveScope(Key("geo", authority.TenantRoot.Host), parent.ScopeKey, DiscoveryScopeKind.Geo, null,
            "sharepoint-live://" + authority.TenantRoot.Host, "geo", null, null, null, null, null));
        return Task.FromResult(Result(parent, new[] { geo }, authority.Sites.Outcome,
            authority.Sites.FailureCode));
    }

    private Task<DiscoveryChildEnumerationResult> EnumerateGeoAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecordAuthoritySurface(parent.ScopeKey, parent.ParentScopeKey, "tenant-site-authority",
            authority.TenantRoot.AbsoluteUri, authority.Sites, authority.Sites.Items.Count,
            "ProductTenantSiteCollectionAuthority", AspxSurfaceApplicability.Applicable);
        var sites = authority.Sites.Items.Select(site => Add(new LiveScope(Key("site", site.Url.AbsoluteUri),
            parent.ScopeKey, DiscoveryScopeKind.SiteCollection, null, site.Url.AbsoluteUri.TrimEnd('/'),
            "site", site.Url, null, null, null, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["siteCollectionId"] = site.SiteId.ToString("D"),
                ["siteUrl"] = site.Url.AbsoluteUri,
                ["rootWebId"] = site.RootWebId.ToString("D"),
                ["graphId"] = site.GraphId ?? string.Empty,
            }))).ToArray();
        return Task.FromResult(Result(parent, sites, authority.Sites.Outcome, authority.Sites.FailureCode));
    }

    private Task<DiscoveryChildEnumerationResult> EnumerateSiteCollectionAsync(LiveScope parent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var siteAuthority = authority.SiteWebs.SingleOrDefault(item =>
            string.Equals(AspxTenantAuthoritySnapshot.Normalize(item.Site.Url),
                AspxTenantAuthoritySnapshot.Normalize(parent.WebUrl), StringComparison.Ordinal));
        if (siteAuthority == null)
        {
            ReferenceCollector.AddGap("site-web-authority-missing:" + parent.ScopeKey);
            return Task.FromResult(Result(parent, Array.Empty<LiveScope>(), DiscoveryTerminalOutcome.Unknown,
                "site_web_authority_missing"));
        }
        RecordAuthoritySurface(parent.ScopeKey, parent.ParentScopeKey, "root-subweb-authority:" + parent.ScopeKey,
            parent.WebUrl.AbsoluteUri, siteAuthority.Webs, siteAuthority.Webs.Items.Count,
            "ProductRootAndSubwebAuthority", AspxSurfaceApplicability.Applicable);
        var scopeByUrl = siteAuthority.Webs.Items.ToDictionary(web =>
            AspxTenantAuthoritySnapshot.Normalize(web.Url), web => Key("web", web.Url.AbsoluteUri), StringComparer.Ordinal);
        var observed = siteAuthority.Webs.Items.Select(web => Add(new LiveScope(scopeByUrl[
                AspxTenantAuthoritySnapshot.Normalize(web.Url)], parent.ScopeKey, DiscoveryScopeKind.Web, null,
            web.Url.AbsoluteUri.TrimEnd('/'), "web", web.Url, null, null, null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["siteCollectionId"] = parent.Metadata?.GetValueOrDefault("siteCollectionId", string.Empty) ?? string.Empty,
                ["siteUrl"] = parent.Metadata?.GetValueOrDefault("siteUrl", parent.WebUrl?.AbsoluteUri ?? string.Empty) ?? string.Empty,
                ["webId"] = web.WebId.ToString("D"),
                ["authorityParentUrl"] = AspxTenantAuthoritySnapshot.Normalize(web.ParentWebUrl) ?? string.Empty,
                ["webTemplateConfiguration"] = web.WebTemplateConfiguration ?? string.Empty,
                ["isRootWeb"] = web.IsRootWeb.ToString(),
            }))).OrderBy(item => item.ScopeKey, StringComparer.Ordinal).ToArray();
        foreach (var web in siteAuthority.Webs.Items)
        {
            var webScopeKey = scopeByUrl[AspxTenantAuthoritySnapshot.Normalize(web.Url)];
            var authorityParentKey = web.ParentWebUrl == null ? parent.ScopeKey :
                scopeByUrl.GetValueOrDefault(AspxTenantAuthoritySnapshot.Normalize(web.ParentWebUrl), parent.ScopeKey);
            RecordWebIdentitySurface(webScopeKey, authorityParentKey, web, siteAuthority.Webs);
        }
        return Task.FromResult(Result(parent, observed, siteAuthority.Webs.Outcome, siteAuthority.Webs.FailureCode));
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
                null, null, null, parent.Metadata)),
        };
        foreach (var item in listResult.Items)
        {
            var listId = PropertyGuid(item, "Id");
            var baseType = PropertyInt(item, "BaseType");
            var template = PropertyInt(item, "BaseTemplate");
            var rootFolder = NestedString(item, "RootFolder", "ServerRelativeUrl");
            var rootFolderUniqueId = NestedString(item, "RootFolder", "UniqueId");
            var title = PropertyString(item, "Title") ?? listId?.ToString("D") ?? "unknown-list";
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["siteCollectionId"] = parent.Metadata?.GetValueOrDefault("siteCollectionId", string.Empty) ?? string.Empty,
                ["siteUrl"] = parent.Metadata?.GetValueOrDefault("siteUrl", parent.WebUrl?.AbsoluteUri ?? string.Empty) ?? string.Empty,
                ["webId"] = parent.Metadata?.GetValueOrDefault("webId", string.Empty) ?? string.Empty,
                ["folderUniqueId"] = rootFolderUniqueId ?? string.Empty,
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
                "web-root-folder", parent.WebUrl, null, null, parent.WebUrl.AbsolutePath, null));
            return Task.FromResult(Result(parent, new[] { child }, DiscoveryTerminalOutcome.Complete));
        }

        var children = new List<LiveScope>();
        var decision = AspxListApplicabilityPolicy.Evaluate(parent.BaseType);
        if (decision.RawLibraryRequired && !string.IsNullOrWhiteSpace(parent.FolderUrl))
            children.Add(Add(new LiveScope(Key("folder", parent.WebUrl.AbsoluteUri, parent.FolderUrl), parent.ScopeKey,
                DiscoveryScopeKind.Folder, DiscoverySourceKind.RawListLibraryFiles, parent.FolderUrl,
                "folder", parent.WebUrl, parent.ListId, parent.BaseType, parent.FolderUrl, parent.Metadata)));
        if (!string.IsNullOrWhiteSpace(parent.FolderUrl))
        {
            var formsFolder = parent.FolderUrl.TrimEnd('/') + "/Forms";
            children.Add(Add(new LiveScope(Key("physical-forms", parent.WebUrl.AbsoluteUri, formsFolder),
                parent.ScopeKey, DiscoveryScopeKind.Folder, DiscoverySourceKind.RawListLibraryFiles,
                formsFolder, "physical-forms", parent.WebUrl, parent.ListId, parent.BaseType,
                formsFolder, parent.Metadata)));
        }
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
        if (parent.SourceKind == DiscoverySourceKind.WebRootFiles)
        {
            var modeledResult = await ReadModeledFolderAsync(parent, cancellationToken).ConfigureAwait(false);
            RecordModeledFolderSurface(parent, modeledResult, files: false);
            if (modeledResult.Outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty)
            {
                var modeledChildren = modeledResult.Folders.Select(item => Add(new LiveScope(
                    Key("web-root-folder", parent.WebUrl.AbsoluteUri, item.ServerRelativeUrl), parent.ScopeKey,
                    DiscoveryScopeKind.Folder, DiscoverySourceKind.WebRootFiles, item.ServerRelativeUrl,
                    "web-root-folder", parent.WebUrl, null, null, item.ServerRelativeUrl,
                    WithMetadata(parent.Metadata, ("folderUniqueId", item.UniqueId))))).ToArray();
                return Result(parent, modeledChildren, modeledResult.Outcome, modeledResult.ErrorCode);
            }
        }
        var endpoint = FolderEndpoint(parent.WebUrl, parent.FolderUrl, "Folders", FoldersSelect);
        var result = await ReadCollectionAsync(parent.ScopeKey, parent.ScopeKey,
            "folders:" + parent.ScopeKey, endpoint, FoldersSelect, string.Empty,
            AspxSurfaceApplicability.Applicable, "RecursiveFolderAuthority", cancellationToken).ConfigureAwait(false);
        var children = result.Items.Select(item =>
        {
            var folderUrl = PropertyString(item, "ServerRelativeUrl");
            return Add(new LiveScope(Key("folder", parent.WebUrl.AbsoluteUri, folderUrl ?? Guid.NewGuid().ToString("N")),
                parent.ScopeKey, DiscoveryScopeKind.Folder, parent.SourceKind, folderUrl ?? "missing-folder-url",
                "folder", parent.WebUrl, parent.ListId, parent.BaseType, folderUrl,
                WithMetadata(parent.Metadata, ("folderUniqueId", PropertyString(item, "UniqueId") ?? string.Empty))));
        }).ToArray();
        return Result(parent, children, result.Validation.Outcome);
    }

    private async IAsyncEnumerable<RawDiscoveryBatch> ReadFolderFilesAsync(LiveScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (scope.SourceKind == DiscoverySourceKind.WebRootFiles)
        {
            var modeled = await ReadModeledFolderAsync(scope, cancellationToken).ConfigureAwait(false);
            RecordModeledFolderSurface(scope, modeled, files: true);
            if (modeled.Outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty)
            {
                var records = modeled.Files.Select(item => new RawDiscoveryRecord(
                    item.UniqueId, item.UniqueId, scope.ScopeKey, item.Name, item.ServerRelativeUrl, true,
                    options.PermissionContext, new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["actualProvider"] = modeled.Provider,
                        ["actualOperation"] = modeled.Operation,
                        ["customizedPageStatus"] = item.CustomizedPageStatus ?? "unknown",
                    },
                    SiteCollectionId: MetadataGuid(scope.Metadata, "siteCollectionId"),
                    WebId: MetadataGuid(scope.Metadata, "webId"),
                    ListId: scope.ListId,
                    FolderUniqueId: MetadataGuid(scope.Metadata, "folderUniqueId") ??
                        (Guid.TryParse(modeled.FolderUniqueId, out var modeledFolderId) ? modeledFolderId : null),
                    HomePage: IsHomePage(scope, item.ServerRelativeUrl),
                    LibraryHidden: MetadataBool(scope.Metadata, "hidden"),
                    CustomizedPageStatusRaw: item.CustomizedPageStatus,
                    ObservationMethod: modeled.Provider + ":" + modeled.Operation,
                    WelcomePageStatus: HomePageStatus(scope, item.ServerRelativeUrl),
                    SiteUrl: MetadataString(scope.Metadata, "siteUrl") ?? scope.WebUrl?.GetLeftPart(UriPartial.Authority),
                    WebUrl: scope.WebUrl?.AbsoluteUri)).ToArray();
                var terminalOutcome = modeled.Outcome == DiscoveryTerminalOutcome.Complete && records.Length == 0
                    ? DiscoveryTerminalOutcome.Empty : modeled.Outcome;
                yield return new RawDiscoveryBatch(0,
                    DiscoveryHash.Of(modeled.Provider, modeled.Operation, scope.FolderUrl),
                    DiscoveryHash.Of(string.Join('|', records.Select(item => item.FileUniqueId))), records,
                    IsTerminal: true, terminalOutcome);
                yield break;
            }
        }
        var endpoint = FolderEndpoint(scope.WebUrl, scope.FolderUrl, "Files", FilesSelect, "ListItemAllFields");
        var result = await ReadCollectionAsync(scope.ScopeKey, scope.ParentScopeKey,
            "files:" + scope.ScopeKey, endpoint, FilesSelect, string.Empty,
            AspxSurfaceApplicability.Applicable, scope.Role == "physical-forms"
                ? "PhysicalFormsTreeAuthority" : "DocumentLibraryFilesAuthority", cancellationToken).ConfigureAwait(false);
        foreach (var page in result.Pages)
        {
            var records = page.Page.Items.Select(item => new RawDiscoveryRecord(
                PropertyString(item, "UniqueId"), PropertyString(item, "UniqueId"),
                scope.ListId?.ToString("D") ?? scope.ScopeKey, PropertyString(item, "Name"),
                PropertyString(item, "ServerRelativeUrl"), true, options.PermissionContext,
                EvidenceMetadata(endpoint, FilesSelect, string.Empty, page.Page.SchemaFlavor,
                    ("customizedPageStatus", PropertyString(item, "CustomizedPageStatus") ?? "unknown"),
                    ("length", PropertyString(item, "Length") ?? "unknown")),
                SiteCollectionId: MetadataGuid(scope.Metadata, "siteCollectionId"),
                WebId: MetadataGuid(scope.Metadata, "webId"),
                ListId: scope.ListId,
                FolderUniqueId: MetadataGuid(scope.Metadata, "folderUniqueId"),
                ListItemId: NestedInt(item, "ListItemAllFields", "Id"),
                HomePage: IsHomePage(scope, PropertyString(item, "ServerRelativeUrl")),
                ContentTypeId: NestedString(item, "ListItemAllFields", "ContentTypeId"),
                PageType: InferPageType(NestedString(item, "ListItemAllFields", "ContentTypeId")),
                LibraryHidden: MetadataBool(scope.Metadata, "hidden"),
                CustomizedPageStatusRaw: PropertyString(item, "CustomizedPageStatus"),
                ObservationMethod: "SharePoint REST folder files",
                WelcomePageStatus: HomePageStatus(scope, PropertyString(item, "ServerRelativeUrl")),
                SiteUrl: MetadataString(scope.Metadata, "siteUrl") ?? scope.WebUrl?.GetLeftPart(UriPartial.Authority),
                WebUrl: scope.WebUrl?.AbsoluteUri))
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
        var dispositionRule = isForm
            ? AspxSystemListFormsPolicy.Create(scope.Metadata, scope.FolderUrl, options.PlatformBuildRef)
            : null;
        var result = await ReadCollectionAsync(scope.ScopeKey, scope.ParentScopeKey,
            name.ToLowerInvariant() + ":" + scope.ScopeKey, endpoint, select, string.Empty,
            scope.BaseType == (int)ListBaseType.DocumentLibrary
                ? AspxSurfaceApplicability.Applicable : AspxSurfaceApplicability.SystemOrVirtualOnly,
            isForm ? "AllListFormsAuthority" : "AllListViewsAuthority", cancellationToken,
            dispositionRule).ConfigureAwait(false);
        if (result.AppliedDispositionRule != null)
        {
            var evidence = new List<string>(result.AppliedDispositionRule.EvidenceRefs)
            {
                EvidenceRef(endpoint, result.Pages.FirstOrDefault()?.Page, select),
            };
            ReferenceCollector.AddReference(Candidate(AspxReferenceSourceKinds.ListForm,
                (scope.ListId?.ToString("D") ?? scope.ScopeKey) + ":forms-authority",
                "SharePoint REST List.Forms system-list disposition", scope.FolderUrl?.TrimEnd('/') + "/Forms",
                AspxReferenceDispositions.ReferenceUnavailable, result.AppliedDispositionRule.ReasonCode,
                null, "system-or-virtual-unknown", evidence.ToArray()));
        }
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
                ("customizedPageStatus", resolved.CustomizedPageStatus ?? "unknown")),
            SiteCollectionId: MetadataGuid(scope.Metadata, "siteCollectionId"),
            WebId: MetadataGuid(scope.Metadata, "webId"),
            ListId: scope.ListId,
            FolderUniqueId: MetadataGuid(scope.Metadata, "folderUniqueId"),
            ListItemId: resolved.ListItemId,
            HomePage: IsHomePage(scope, resolved.ServerRelativeUrl ?? locator),
            ContentTypeId: resolved.ContentTypeId,
            PageType: InferPageType(resolved.ContentTypeId),
            LibraryHidden: MetadataBool(scope.Metadata, "hidden"),
            CustomizedPageStatusRaw: resolved.CustomizedPageStatus,
            ObservationMethod: method,
            WelcomePageStatus: HomePageStatus(scope, resolved.ServerRelativeUrl ?? locator),
            SiteUrl: MetadataString(scope.Metadata, "siteUrl") ?? scope.WebUrl?.GetLeftPart(UriPartial.Authority),
            WebUrl: scope.WebUrl?.AbsoluteUri);
        return (Candidate(sourceKind, sourceObjectIdentity, method, locator, dispositionValue, null,
            resolved.FileUniqueId, contentOrigin, evidenceRef, resolved.EvidenceRef), physical);
    }

    private async Task AcquireWelcomePageAsync(LiveScope web, CancellationToken cancellationToken)
    {
        var client = await clientFactory.GetAsync(web.WebUrl, cancellationToken).ConfigureAwait(false);
        var result = await client.ReadWelcomePageAsync(cancellationToken).ConfigureAwait(false);
        var outcome = result.Outcome == DiscoveryTerminalOutcome.Complete && string.IsNullOrWhiteSpace(result.Value)
            ? DiscoveryTerminalOutcome.Empty : result.Outcome;
        RecordModeledValueSurface(web, "welcome-page:" + web.ScopeKey, result, outcome,
            AspxSurfaceApplicability.SystemOrVirtualOnly, "PnPCoreWelcomePageAuthority");
        if (outcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or
            DiscoveryTerminalOutcome.Unknown)
        {
            ReferenceCollector.AddReference(Candidate(AspxReferenceSourceKinds.WebWelcomePage, web.ScopeKey,
                result.Provider + ":" + result.Operation, null,
                AspxReferenceDispositions.ReferenceUnavailable,
                result.ErrorCode ?? (outcome == DiscoveryTerminalOutcome.Denied
                    ? "welcome_page_denied" : "welcome_page_failed"),
                null, "unavailable", result.EvidenceRef));
            return;
        }
        var value = result.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            ReferenceCollector.AddReference(Candidate(AspxReferenceSourceKinds.WebWelcomePage, web.ScopeKey,
                result.Provider + ":" + result.Operation, value, AspxReferenceDispositions.NonAspx,
                "welcome_page_success_empty", null, "empty", result.EvidenceRef));
            return;
        }
        var absolutePath = value.StartsWith('/') ? value : web.WebUrl.AbsolutePath.TrimEnd('/') + "/" + value.TrimStart('/');
        welcomePageByWeb[web.WebUrl.AbsoluteUri.TrimEnd('/')] = NormalizeServerRelativePath(absolutePath);
        var resolved = await ResolveReferenceAsync(web with { ListId = null }, isForm: false,
            web.ScopeKey, absolutePath, result.EvidenceRef,
            cancellationToken).ConfigureAwait(false);
        ReferenceCollector.AddReference(resolved.Candidate with
        {
            SourceKind = AspxReferenceSourceKinds.WebWelcomePage,
            AcquisitionMethod = result.Provider + ":" + result.Operation + " + PnP.Core file resolution",
        });
    }

    private void RecordListApplicability(LiveScope web, LiveScope list, int? template)
    {
        var decision = AspxListApplicabilityPolicy.Evaluate(list.BaseType);
        var applicability = decision.Applicability;
        var counterexample = decision.RuntimeCounterexampleState;
        var outcome = decision.Outcome;
        var expectedState = outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty
            ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown;
        var evidence = new[]
        {
            $"actual-BaseType={list.BaseType?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}",
            $"BaseTemplate={template?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}",
            $"surface-family={ClassifyListSurface(list, template)}",
            $"Hidden={list.Metadata?.GetValueOrDefault("hidden", "missing") ?? "missing"}",
            $"IsCatalog={list.Metadata?.GetValueOrDefault("isCatalog", "missing") ?? "missing"}",
            "Applicability is selected from actual BaseType; template/catalog flags remain explicit provenance.",
        };
        ReferenceCollector.AddSurface(SurfaceRow(web.ScopeKey, web.ScopeKey,
            "list-applicability:" + list.ScopeKey, list.Locator, string.Empty, string.Empty,
            "ActualListBaseTypeAuthority", applicability, outcome,
            expectedState == AspxExpectedCountState.Known ? 1 : null, expectedState,
            1, DiscoveryHash.Of("list-applicability", list.ScopeKey), 0, evidence,
            applicability == AspxSurfaceApplicability.Applicable ? "raw-files+physical-forms+forms+views"
                : "forms+views"), Array.Empty<AspxPaginationPageReceipt>(),
            outcome == DiscoveryTerminalOutcome.Unknown
                ? new[] { counterexample == AspxRuntimeCounterexampleState.Observed
                    ? "not_applicable_runtime_counterexample" : "invalid_or_unspecified_base_type" }
                : Array.Empty<string>());

        RecordMatrixDisposition(web, list, "document-library-files",
            decision.RawLibraryRequired ? AspxSurfaceApplicability.Applicable
                : list.BaseType == null ? AspxSurfaceApplicability.Unknown
                : AspxSurfaceApplicability.SystemOrVirtualOnly,
            list.BaseType == null ? DiscoveryTerminalOutcome.Unknown : DiscoveryTerminalOutcome.Complete,
            "DocumentLibraryFilesAuthority", evidence);
        RecordMatrixDisposition(web, list, "physical-forms-tree",
            string.IsNullOrWhiteSpace(list.FolderUrl) ? AspxSurfaceApplicability.Unknown
                : AspxSurfaceApplicability.Applicable,
            string.IsNullOrWhiteSpace(list.FolderUrl) ? DiscoveryTerminalOutcome.Unknown
                : DiscoveryTerminalOutcome.Complete,
            "PhysicalFormsTreeAuthority", evidence);
        RecordMatrixDisposition(web, list, "list-forms", decision.Applicability, decision.Outcome,
            "AllListFormsAuthority", evidence);
        RecordMatrixDisposition(web, list, "list-views", decision.Applicability, decision.Outcome,
            "AllListViewsAuthority", evidence);
    }

    private void RecordMatrixDisposition(LiveScope web, LiveScope list, string surface,
        AspxSurfaceApplicability applicability, DiscoveryTerminalOutcome outcome, string adapter,
        IReadOnlyList<string> evidence)
    {
        var known = outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty;
        var row = SurfaceRow(web.ScopeKey, web.ScopeKey,
            $"applicability:{list.ScopeKey}:{surface}", list.Locator, string.Empty, string.Empty,
            "ReviewedApplicabilityMatrix", applicability, outcome, known ? 1 : null,
            known ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown, known ? 1 : 0,
            DiscoveryHash.Of("applicability", list.ScopeKey, surface, applicability.ToString(),
                outcome.ToString()), 0, evidence, adapter) with
        {
            ActualMethod = "CLASSIFY",
            ClassificationEffect = applicability == AspxSurfaceApplicability.Applicable
                ? "physical-and-reference" : applicability == AspxSurfaceApplicability.SystemOrVirtualOnly
                    ? "reference-first" : "fail-closed",
        };
        ReferenceCollector.AddSurface(row,
            Array.Empty<AspxPaginationPageReceipt>(), known ? Array.Empty<string>()
                : new[] { $"applicability:{list.ScopeKey}:{surface}:unknown" });
    }

    private static string ClassifyListSurface(LiveScope list, int? template)
    {
        var catalog = string.Equals(list.Metadata?.GetValueOrDefault("isCatalog"), "True",
            StringComparison.OrdinalIgnoreCase);
        if (catalog || list.FolderUrl?.Contains("/_catalogs/", StringComparison.OrdinalIgnoreCase) == true)
            return template == 116 || list.FolderUrl?.Contains("masterpage", StringComparison.OrdinalIgnoreCase) == true
                ? "catalog-page-layout-library" : "catalog-library";
        if (template is 119 or 850) return "page-library";
        return list.BaseType == (int)ListBaseType.DocumentLibrary ? "document-library" : "list";
    }

    private void RecordAuthoritySurface<T>(string scopeKey, string parentScopeKey, string surfaceId,
        string locator, AspxAuthorityCollection<T> result, int observed, string authorityKind,
        AspxSurfaceApplicability applicability)
    {
        var known = result.IsTerminalSuccess && !result.ContinuationRemaining;
        int? expected = known ? observed : null;
        var outcome = known && observed == 0 ? DiscoveryTerminalOutcome.Empty : result.Outcome;
        var evidence = new List<string>
        {
            "provider=" + result.Provider,
            "operation=" + result.Operation,
            "actualFilter=" + (result.ActualFilter ?? string.Empty),
            "continuationRemaining=" + result.ContinuationRemaining,
        };
        evidence.AddRange((result.Exclusions ?? Array.Empty<string>()).Select(value => "exclusion=" + value));
        if (!string.IsNullOrWhiteSpace(result.FailureCode)) evidence.Add("failureCode=" + result.FailureCode);
        if (!string.IsNullOrWhiteSpace(result.FailureDetail)) evidence.Add("failure=" + result.FailureDetail);
        var row = SurfaceRow(scopeKey, parentScopeKey, surfaceId, locator, result.Provider,
            result.ActualFilter ?? string.Empty, authorityKind, applicability, outcome, expected,
            known ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown, observed,
            DiscoveryHash.Of(authority.AuthorityHash, surfaceId, result.Provider, result.Operation,
                result.ActualFilter ?? string.Empty, observed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            result.ContinuationRemaining ? 1 : 0, evidence, authorityKind,
            known && observed == 0 ? "AuthorityTerminalZero" : null,
            known && observed == 0 ? locator : null) with
        {
            ActualMethod = "INVOKE",
            ActualEndpoint = result.Provider + ":" + result.Operation,
            AsOfUtc = result.CompletedAtUtc,
        };
        var gaps = new List<string>();
        if (!known || (result.Exclusions?.Count ?? 0) > 0)
            gaps.Add(surfaceId + ":authority_incomplete");
        if (result.ContinuationRemaining) gaps.Add(surfaceId + ":authority_continuation_remaining");
        ReferenceCollector.AddSurface(row, Array.Empty<AspxPaginationPageReceipt>(), gaps);
    }

    private void RecordWebIdentitySurface(string scopeKey, string parentScopeKey, AspxAuthorityWeb web,
        AspxAuthorityCollection<AspxAuthorityWeb> result)
    {
        var known = result.IsTerminalSuccess && !result.ContinuationRemaining;
        var evidence = new[]
        {
            "webId=" + web.WebId.ToString("D"),
            "serverRelativeUrl=" + (web.ServerRelativeUrl ?? string.Empty),
            "parentWebUrl=" + (AspxTenantAuthoritySnapshot.Normalize(web.ParentWebUrl) ?? string.Empty),
            "isRootWeb=" + web.IsRootWeb,
            "provider=" + result.Provider,
            "operation=" + result.Operation,
            "actualFilter=" + (result.ActualFilter ?? string.Empty),
        };
        var row = SurfaceRow(scopeKey, parentScopeKey, "web-identity:" + scopeKey, web.Url.AbsoluteUri,
            result.Provider, result.ActualFilter ?? string.Empty, "ProductRootAndSubwebIdentity",
            AspxSurfaceApplicability.Applicable, result.Outcome, known ? 1 : null,
            known ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown, 1,
            DiscoveryHash.Of(authority.AuthorityHash, scopeKey, parentScopeKey, web.Url.AbsoluteUri),
            result.ContinuationRemaining ? 1 : 0, evidence, result.Operation) with
        {
            ActualMethod = "INVOKE",
            ActualEndpoint = result.Provider + ":" + result.Operation,
            AsOfUtc = result.CompletedAtUtc,
        };
        ReferenceCollector.AddSurface(row, Array.Empty<AspxPaginationPageReceipt>(), known
            ? Array.Empty<string>() : new[] { "web-identity:" + scopeKey + ":authority_incomplete" });
    }

    private async Task<SharePointModeledFolderResult> ReadModeledFolderAsync(LiveScope scope,
        CancellationToken cancellationToken)
    {
        var key = AspxTenantAuthoritySnapshot.Normalize(scope.WebUrl) + "|" + scope.FolderUrl;
        if (modeledFolders.TryGetValue(key, out var existing)) return existing;
        var client = await clientFactory.GetAsync(scope.WebUrl, cancellationToken).ConfigureAwait(false);
        var result = await client.ReadFolderAsync(scope.FolderUrl, cancellationToken).ConfigureAwait(false);
        modeledFolders[key] = result;
        return result;
    }

    private void RecordModeledFolderSurface(LiveScope scope, SharePointModeledFolderResult result, bool files)
    {
        var observed = files ? result.Files.Count : result.Folders.Count;
        var known = result.Outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty;
        var outcome = known && observed == 0 ? DiscoveryTerminalOutcome.Empty : result.Outcome;
        var select = files ? "UniqueId,Name,ServerRelativeUrl,CustomizedPageStatus"
            : "UniqueId,Name,ServerRelativeUrl";
        var surfaceId = (files ? "web-root-files:" : "web-root-folders:") + scope.ScopeKey;
        var row = SurfaceRow(scope.ScopeKey, scope.ParentScopeKey, surfaceId, scope.FolderUrl,
            select, string.Empty, "PnPCoreModeledWebRootAuthority", AspxSurfaceApplicability.Applicable,
            outcome, known ? observed : null,
            known ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown, observed,
            DiscoveryHash.Of(result.Provider, result.Operation, scope.FolderUrl, files.ToString(),
                observed.ToString(System.Globalization.CultureInfo.InvariantCulture)), 0,
            new[] { result.EvidenceRef, "provider=" + result.Provider, "operation=" + result.Operation },
            files ? "PnPCoreWebRootFilesAuthority" : "PnPCoreWebRootFoldersAuthority",
            known && observed == 0 ? "AuthorityTerminalZero" : null,
            known && observed == 0 ? scope.FolderUrl : null) with
        {
            ActualMethod = "MODEL",
            ActualEndpoint = result.Provider + ":" + result.Operation,
            AsOfUtc = result.ReceivedAtUtc,
        };
        ReferenceCollector.AddSurface(row, Array.Empty<AspxPaginationPageReceipt>(), known
            ? Array.Empty<string>() : new[] { surfaceId + ":" + (result.ErrorCode ?? "modeled_authority_incomplete") });
    }

    private void RecordModeledValueSurface(LiveScope scope, string surfaceId, SharePointModeledValue result,
        DiscoveryTerminalOutcome outcome, AspxSurfaceApplicability applicability, string adapter)
    {
        var known = outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty;
        var observed = string.IsNullOrWhiteSpace(result.Value) ? 0 : 1;
        var row = SurfaceRow(scope.ScopeKey, scope.ParentScopeKey, surfaceId, scope.WebUrl.AbsoluteUri,
            "WelcomePage", string.Empty, "PnPCoreModeledWelcomePageAuthority", applicability, outcome,
            known ? observed : null, known ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown,
            observed, DiscoveryHash.Of(result.Provider, result.Operation, result.Value ?? string.Empty), 0,
            new[] { result.EvidenceRef, "provider=" + result.Provider, "operation=" + result.Operation },
            adapter, known && observed == 0 ? "AuthorityTerminalZero" : null,
            known && observed == 0 ? scope.WebUrl.AbsoluteUri : null) with
        {
            ActualMethod = "MODEL",
            ActualEndpoint = result.Provider + ":" + result.Operation,
            AsOfUtc = result.ReceivedAtUtc,
        };
        ReferenceCollector.AddSurface(row, Array.Empty<AspxPaginationPageReceipt>(), known
            ? Array.Empty<string>() : new[] { surfaceId + ":" + (result.ErrorCode ?? "modeled_authority_incomplete") });
    }

    private async Task<LiveCollectionResult> ReadCollectionAsync(string scopeKey, string parentScopeKey,
        string surfaceId, Uri initialEndpoint, string select, string filter,
        AspxSurfaceApplicability applicability, string adapter, CancellationToken cancellationToken,
        AspxSurfaceDispositionRule dispositionRule = null)
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
                    DiscoveryTerminalOutcome.Failed, "transport_failure",
                    SharePointSemanticDetectorResults.None, DateTimeOffset.UtcNow,
                    AttemptCount: 1, AttemptLimit: 1);
            }
            var nextLink = page.NextLink;
            var terminal = string.IsNullOrWhiteSpace(nextLink) || page.Outcome != DiscoveryTerminalOutcome.Complete;
            var nextUri = terminal ? null : ResolveNext(initialEndpoint, nextLink);
            var receipt = new AspxPaginationPageReceipt(AspxAcquisitionVersions.PaginationReceipt,
                surfaceId, authority.AuthorityRevision,
                DiscoveryHash.Of(initialEndpoint.GetLeftPart(UriPartial.Path), select, filter ?? string.Empty),
                "GET", AspxDurableRequestEvidence.Endpoint(page.RequestUri), select, filter ?? string.Empty, ordinal,
                AspxPaginationContract.TokenHash(requestToken), page.Items.Count,
                AspxPaginationContract.TokenHash(nextLink), page.ResponseDigest,
                page.StatusCode == 0 ? null : (int)page.StatusCode, page.SemanticDetectorResult,
                page.AttemptCount, page.AttemptLimit, page.RequestId, page.CorrelationId, page.ErrorCode,
                terminal, page.ReceivedAtUtc == default ? DateTimeOffset.UtcNow : page.ReceivedAtUtc);
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
        var appliedDispositionRule = dispositionRule != null && pages.Any(page => dispositionRule.Applies(page.Page))
            ? dispositionRule : null;
        var absenceKind = outcome == DiscoveryTerminalOutcome.Empty ? "AuthorityTerminalZero" : null;
        var row = SurfaceRow(scopeKey, parentScopeKey, surfaceId, initialEndpoint.AbsoluteUri, select,
            filter ?? string.Empty, "SharePointRestCollectionAuthority", applicability, outcome,
            expected, expectedState, items.Count, validation.ChainHash, validation.OutstandingTokenCount,
            pages.Select(page => EvidenceRef(initialEndpoint, page.Page, select)).ToArray(), adapter,
            absenceKind, absenceKind == null ? null : initialEndpoint.AbsoluteUri, appliedDispositionRule);
        ReferenceCollector.AddSurface(row, pages.Select(page => page.Receipt).ToArray(),
            validation.GapCodes.Select(code => surfaceId + ":" + code).ToArray());
        return new(items, pages, validation with { Outcome = outcome }, appliedDispositionRule);
    }

    private AspxSurfaceDenominatorRow SurfaceRow(string scopeKey, string parentScopeKey, string surfaceId,
        string endpoint, string select, string filter, string authorityKind,
        AspxSurfaceApplicability applicability, DiscoveryTerminalOutcome outcome, int? expected,
        AspxExpectedCountState expectedState, int observed, string chainHash, int outstanding,
        IReadOnlyList<string> evidenceRefs, string adapter, string absenceKind = null, string absenceRef = null,
        AspxSurfaceDispositionRule dispositionRule = null) =>
        new(AspxAcquisitionVersions.SurfaceContract, Guid.Empty, null, null, scopeKey, parentScopeKey,
            surfaceId, applicability, dispositionRule?.RuleId, dispositionRule?.RuleVersion,
            dispositionRule?.RuleHash, dispositionRule?.ReviewRef, null, dispositionRule?.PlatformBinding,
            applicability == AspxSurfaceApplicability.NotApplicable
                ? AspxRuntimeCounterexampleState.Observed : AspxRuntimeCounterexampleState.NoneObserved,
            authorityKind, endpoint, authority.AuthorityRevision, authority.AuthorityHash,
            "GET", endpoint, select, filter, options.VisibilityBoundary, options.PermissionContext,
            expected, expectedState, observed, outcome,
            outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty ? "satisfied"
                : outcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or
                    DiscoveryTerminalOutcome.Truncated or DiscoveryTerminalOutcome.Cancelled ? "incomplete" : "unknown",
            outstanding != 0, chainHash, outstanding, absenceKind, absenceRef,
            null, null, null, null, null, null, null, DateTimeOffset.UtcNow,
            (evidenceRefs ?? Array.Empty<string>()).Concat(dispositionRule?.EvidenceRefs ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal).ToArray(), adapter,
            dispositionRule?.ClassificationEffect ??
                (applicability == AspxSurfaceApplicability.Applicable ? "physical-and-reference"
                    : applicability == AspxSurfaceApplicability.SystemOrVirtualOnly ? "reference-first" : "fail-closed"));

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
        AspxDurableRequestEvidence.Reference(endpoint, page, select);

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
        scope.PermissionContext, Required: true, Metadata: scope.Metadata);

    private LiveScope Add(LiveScope scope)
    {
        scopes.TryAdd(scope.ScopeKey, scope);
        return scopes[scope.ScopeKey];
    }

    private static AspxTenantAuthoritySnapshot LegacyDeclaredAuthority(
        SharePointLiveAspxDiscoveryOptions options)
    {
        var sites = (options.SiteCollectionUrls ?? Array.Empty<Uri>())
            .DistinctBy(AspxTenantAuthoritySnapshot.Normalize, StringComparer.Ordinal)
            .OrderBy(AspxTenantAuthoritySnapshot.Normalize, StringComparer.Ordinal)
            .Select(url => new AspxAuthoritySite(Guid.Empty, Guid.Empty, url, null, null)).ToArray();
        var siteResult = new AspxAuthorityCollection<AspxAuthoritySite>(
            sites.Length == 0 ? DiscoveryTerminalOutcome.Empty : DiscoveryTerminalOutcome.Complete,
            sites, "Assessment.Legacy", "DeclaredSiteArguments", "--site",
            new[] { "tenant-site-denominator-not-enumerated" }, null, null,
            ContinuationRemaining: false, DateTimeOffset.UtcNow);
        var webs = sites.Select(site => new AspxSiteWebAuthority(site,
            new AspxAuthorityCollection<AspxAuthorityWeb>(DiscoveryTerminalOutcome.Complete,
                new[] { new AspxAuthorityWeb(site.RootWebId, site.Url, site.Url.AbsolutePath,
                    null, null, IsRootWeb: true) }, "Assessment.Legacy", "DeclaredRootWeb",
                "subweb-authority-not-enumerated", new[] { "subweb-authority-not-enumerated" },
                null, null, ContinuationRemaining: false, DateTimeOffset.UtcNow))).ToArray();
        var tenantRoot = sites.FirstOrDefault()?.Url is { } first
            ? new Uri(first.GetLeftPart(UriPartial.Authority))
            : new Uri("https://invalid.sharepoint.com");
        var snapshot = AspxTenantAuthoritySnapshot.Freeze(AspxScopeModes.DeclaredSubset,
            tenantRoot, siteResult, webs);
        return snapshot with
        {
            AuthorityRevision = options.AuthorityRevision ?? snapshot.AuthorityRevision,
            AuthorityHash = options.AuthorityHash ?? snapshot.AuthorityHash,
            TenantVisibilityVerified = false,
        };
    }

    private string Key(params string[] values) => DiscoveryHash.Of(values)[..24];
    private static Uri Endpoint(Uri webUrl, string relative) => new(webUrl.AbsoluteUri.TrimEnd('/') + "/" + relative.TrimStart('/'));
    private static Uri FolderEndpoint(Uri webUrl, string folderUrl, string child, string select, string expand = null)
    {
        var escaped = (folderUrl ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
        var query = $"$select={select}" + (string.IsNullOrWhiteSpace(expand) ? string.Empty : $"&$expand={expand}");
        return Endpoint(webUrl, $"_api/web/GetFolderByServerRelativePath(decodedurl='{escaped}')/{child}?{query}");
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
    private static int? NestedInt(JsonElement item, string parent, string child) =>
        PnPContextSharePointAspxRestClient.TryProperty(item, parent, out var nested) ? PropertyInt(nested, child) : null;
    private static Guid? MetadataGuid(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata != null && metadata.TryGetValue(key, out var value) && Guid.TryParse(value, out var parsed) ? parsed : null;
    private static string MetadataString(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata != null && metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    private static bool? MetadataBool(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata != null && metadata.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : null;
    private static string InferPageType(string contentTypeId)
    {
        if (string.IsNullOrWhiteSpace(contentTypeId)) return null;
        if (contentTypeId.StartsWith("0x0101009D1CB255DA76424F860D91F20E6C4118", StringComparison.OrdinalIgnoreCase))
            return "ModernSitePage";
        if (contentTypeId.StartsWith("0x01010007FF3E057FA8AB4AA42FCB67B453FFC1", StringComparison.OrdinalIgnoreCase))
            return "PublishingPage";
        if (contentTypeId.StartsWith("0x01010901", StringComparison.OrdinalIgnoreCase)) return "WebPartPage";
        if (contentTypeId.StartsWith("0x010109", StringComparison.OrdinalIgnoreCase)) return "BasicPage";
        if (contentTypeId.StartsWith("0x010108", StringComparison.OrdinalIgnoreCase)) return "WikiPage";
        if (contentTypeId.StartsWith("0x010105", StringComparison.OrdinalIgnoreCase)) return "MasterPage";
        return "OtherAspxContentType";
    }
    private static IReadOnlyDictionary<string, string> WithMetadata(
        IReadOnlyDictionary<string, string> metadata, params (string Key, string Value)[] additions)
    {
        var copy = metadata == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        foreach (var (key, value) in additions) copy[key] = value ?? string.Empty;
        return copy;
    }
    private bool? IsHomePage(LiveScope scope, string serverRelativeUrl)
    {
        if (scope.WebUrl == null || !welcomePageByWeb.TryGetValue(scope.WebUrl.AbsoluteUri.TrimEnd('/'), out var welcomePage))
            return null;
        return string.Equals(welcomePage, NormalizeServerRelativePath(serverRelativeUrl), StringComparison.OrdinalIgnoreCase);
    }
    private string HomePageStatus(LiveScope scope, string serverRelativeUrl) => IsHomePage(scope, serverRelativeUrl) switch
    {
        true => "matched",
        false => "not-matched",
        null => "unknown",
    };
    private static string NormalizeServerRelativePath(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : "/" + value.Replace('\\', '/').Trim().TrimStart('/');
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
        AspxPaginationValidation Validation,
        AspxSurfaceDispositionRule AppliedDispositionRule);
}

internal sealed class LiveRawDiscoverySource : IRawDiscoverySource
{
    private readonly Func<CancellationToken, IAsyncEnumerable<RawDiscoveryBatch>> factory;
    internal LiveRawDiscoverySource(Func<CancellationToken, IAsyncEnumerable<RawDiscoveryBatch>> factory) =>
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync(CancellationToken cancellationToken = default) =>
        factory(cancellationToken);
}
