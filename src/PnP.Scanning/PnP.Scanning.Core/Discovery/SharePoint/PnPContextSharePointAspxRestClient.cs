using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PnP.Scanning.Core.Discovery;

internal sealed class PnPContextSharePointAspxRestClient : ISharePointAspxRestClient
{
    private const string JsonAcceptMediaType = "application/json;odata=nometadata";
    private const string BoundedJsonErrorSchema = "bounded-json-error";
    private const string FileResolutionProvider = "PnP.Core:IWeb.GetFileByServerRelativeUrlOrDefaultAsync";
    private const string SetupPathProperty = "vti_setuppath";

    private static class JsonReadErrors
    {
        internal const string RedirectOrUriDrift = "json_redirect_or_response_uri_drift";
        internal const string Denied = "json_http_denied";
        internal const string SizeOrPartial = "json_response_size_or_partial";
        internal const string SizeLimit = "json_response_size_limit";
        internal const string IncompleteBody = "json_response_incomplete_body";
        internal const string Timeout = "json_response_timeout";
        internal const string IncompleteStream = "json_response_incomplete_stream";
        internal const string TransportFailure = "json_transport_failure";
        internal const string ThrottleExhausted = "json_throttle_attempts_exhausted";
        internal const string ThrottleBudget = "json_throttle_delay_exceeds_budget";
    }

    private static class JsonEnvelope
    {
        internal const string Value = "value";
        internal const string NextLink = "@odata.nextLink";
        internal const string LegacyNextLink = "odata.nextLink";
        internal const string VerboseData = "d";
        internal const string Results = "results";
        internal const string VerboseNext = "__next";
        internal const string NoMetadataFlavor = "odata-nometadata";
        internal const string VerboseFlavor = "odata-verbose";
        internal const string VerboseObjectFlavor = "odata-verbose-object";
        internal const string ArrayFlavor = "json-array";
        internal const string ObjectFlavor = "json-object";
    }

    private readonly PnPContext context;
    private readonly bool ownsContext;
    private readonly HttpClient jsonTransport;
    private readonly SharePointRestThrottlePolicy throttlePolicy;
    private readonly HttpClient ownedJsonTransport = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false
    });

    // Optional transport is explicitly borrowed; its owner (for example an offline SDK fixture)
    // retains its lifetime. Production defaults to our owned no-redirect/no-cookie client.
    /// <summary>Creates a bounded SDK-authenticated JSON reader while retaining explicit context and borrowed transport ownership.</summary>
    internal PnPContextSharePointAspxRestClient(PnPContext context, bool ownsContext = true, HttpClient jsonTransport = null,
        SharePointRestThrottlePolicy throttlePolicy = null)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.ownsContext = ownsContext;
        this.jsonTransport = jsonTransport ?? ownedJsonTransport;
        this.throttlePolicy = throttlePolicy ?? SharePointRestThrottlePolicy.Default;
    }

    public Uri WebUrl => context.Uri;

    /// <summary>Requests one bounded JSON page using context authentication and a shared deadline across safe throttle retries.</summary>
    public Task<SharePointRestPage> GetPageAsync(Uri requestUri,
        CancellationToken cancellationToken = default) => ReadBoundedPageAsync(jsonTransport, context.Uri,
            requestUri, (request, token) => context.AuthenticationProvider.AuthenticateRequestAsync(context.Uri, request),
            cancellationToken: cancellationToken, throttlePolicy: throttlePolicy);

    // Thin fake-HTTP seam; production defaults to the owned no-redirect/no-cookie client above.
    /// <summary>Enforces authenticated web-API scope, byte limits, and one deadline across fresh 429/503 attempts and server-directed delays.</summary>
    internal static async Task<SharePointRestPage> ReadBoundedPageAsync(HttpClient client, Uri web, Uri uri,
        Func<HttpRequestMessage, CancellationToken, Task> authenticate, int maxBytes = 4 * 1024 * 1024,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default,
        SharePointRestThrottlePolicy throttlePolicy = null)
    {
        SharePointAspxUrlPolicy.ValidateWeb(web);
        if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort ||
            !string.Equals(uri.Authority, web.Authority, StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.StartsWith(web.AbsolutePath.TrimEnd('/') + "/_api/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("JSON requests must stay in the authenticated web API.");
        }
        var budget = timeout ?? TimeSpan.FromSeconds(20);
        if (maxBytes is < 1 or > 16 * 1024 * 1024 || budget <= TimeSpan.Zero || budget > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
        var policy = throttlePolicy ?? SharePointRestThrottlePolicy.Default;
        var started = policy.Clock.GetTimestamp();
        var evidence = new SharePointRestPage(uri, 0, Array.Empty<JsonElement>(), null, null,
            BoundedJsonErrorSchema, DiscoveryTerminalOutcome.Unknown, ReceivedAtUtc: policy.Clock.GetUtcNow(),
            AttemptCount: 0, AttemptLimit: policy.AttemptLimit);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (policy.Clock.GetElapsedTime(started) >= budget)
                {
                    return JsonFailure(evidence, DiscoveryTerminalOutcome.Truncated, JsonReadErrors.Timeout, policy.Clock);
                }
                evidence = evidence with
                {
                    AttemptCount = evidence.AttemptCount + 1,
                    StatusCode = 0,
                    ReceivedAtUtc = policy.Clock.GetUtcNow(),
                    RequestId = null,
                    CorrelationId = null
                };
                TimeSpan retryDelay;
                // The failed response, its content/stream, and the request leave scope before any retry delay.
                using (var request = CreateGetRequest(uri))
                {
                    await authenticate(request, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (policy.Clock.GetElapsedTime(started) >= budget)
                    {
                        return JsonFailure(evidence, DiscoveryTerminalOutcome.Truncated, JsonReadErrors.Timeout, policy.Clock);
                    }
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        deadline.Token).ConfigureAwait(false);
                    evidence = evidence with
                    {
                        StatusCode = response.StatusCode,
                        ReceivedAtUtc = policy.Clock.GetUtcNow(),
                        RequestId = Header(response, "SPRequestGuid", "request-id", "x-ms-request-id"),
                        CorrelationId = Header(response, "x-ms-correlation-id", "x-ms-correlation-request-id", "client-request-id")
                    };
                    deadline.Token.ThrowIfCancellationRequested();
                    if ((int)response.StatusCode is >= 300 and <= 399 || response.RequestMessage?.RequestUri == null ||
                        !response.RequestMessage.RequestUri.IsAbsoluteUri ||
                        !string.Equals(response.RequestMessage.RequestUri.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal))
                    {
                        return JsonFailure(evidence, DiscoveryTerminalOutcome.Unknown, JsonReadErrors.RedirectOrUriDrift, policy.Clock);
                    }
                    if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    {
                        return JsonFailure(evidence, DiscoveryTerminalOutcome.Denied, JsonReadErrors.Denied, policy.Clock);
                    }
                    long? length = response.Content.Headers.ContentLength;
                    if (length > maxBytes || response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        return JsonFailure(evidence, DiscoveryTerminalOutcome.Truncated, JsonReadErrors.SizeOrPartial, policy.Clock);
                    }
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    {
                        if (evidence.AttemptCount >= policy.AttemptLimit)
                        {
                            return JsonFailure(evidence, DiscoveryTerminalOutcome.Failed, JsonReadErrors.ThrottleExhausted, policy.Clock);
                        }
                        // No need to read or persist an error body before disposing the throttled response.
                        retryDelay = policy.GetRetryDelay(response, evidence.ReceivedAtUtc, evidence.AttemptCount);
                    }
                    else
                    {
                        using var buffer = new MemoryStream();
                        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                        byte[] chunk = new byte[Math.Min(maxBytes + 1, 8192)];
                        while (true)
                        {
                            deadline.Token.ThrowIfCancellationRequested();
                            if (policy.Clock.GetElapsedTime(started) >= budget)
                            {
                                return JsonFailure(evidence, DiscoveryTerminalOutcome.Truncated, JsonReadErrors.Timeout, policy.Clock);
                            }
                            int count = await stream.ReadAsync(chunk.AsMemory(0,
                                Math.Min(chunk.Length, maxBytes + 1 - (int)buffer.Length)), deadline.Token).ConfigureAwait(false);
                            if (count == 0)
                            {
                                break;
                            }
                            buffer.Write(chunk, 0, count);
                            if (buffer.Length > maxBytes)
                            {
                                return JsonFailure(evidence, DiscoveryTerminalOutcome.Truncated, JsonReadErrors.SizeLimit, policy.Clock);
                            }
                        }
                        deadline.Token.ThrowIfCancellationRequested();
                        if (policy.Clock.GetElapsedTime(started) >= budget)
                        {
                            return JsonFailure(evidence, DiscoveryTerminalOutcome.Truncated, JsonReadErrors.Timeout, policy.Clock);
                        }
                        if (length.HasValue && length.Value != buffer.Length)
                        {
                            return JsonFailure(evidence, DiscoveryTerminalOutcome.Truncated, JsonReadErrors.IncompleteBody, policy.Clock);
                        }
                        var page = SharePointRestResponseParser.Parse(uri, response.StatusCode, buffer.ToArray(),
                            response.Content.Headers.ContentType?.MediaType, evidence.RequestId, evidence.CorrelationId,
                            evidence.ReceivedAtUtc, evidence.AttemptCount, evidence.AttemptLimit);
                        deadline.Token.ThrowIfCancellationRequested();
                        return policy.Clock.GetElapsedTime(started) >= budget
                            ? JsonFailure(evidence, DiscoveryTerminalOutcome.Truncated, JsonReadErrors.Timeout, policy.Clock)
                            : page;
                    }
                }
                deadline.Token.ThrowIfCancellationRequested();
                if (retryDelay >= budget - policy.Clock.GetElapsedTime(started))
                {
                    return JsonFailure(evidence, DiscoveryTerminalOutcome.Failed, JsonReadErrors.ThrottleBudget, policy.Clock);
                }
                await policy.DelayAsync(retryDelay, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return JsonFailure(evidence, DiscoveryTerminalOutcome.Truncated, JsonReadErrors.Timeout, policy.Clock);
        }
        catch (IOException)
        {
            return JsonFailure(evidence, DiscoveryTerminalOutcome.Truncated, JsonReadErrors.IncompleteStream, policy.Clock);
        }
        catch (HttpRequestException)
        {
            return JsonFailure(evidence, DiscoveryTerminalOutcome.Failed, JsonReadErrors.TransportFailure, policy.Clock);
        }
    }

    /// <summary>Preserves the final response receipt, or timestamps a response-free failure, without retaining source bytes.</summary>
    private static SharePointRestPage JsonFailure(SharePointRestPage evidence, DiscoveryTerminalOutcome outcome, string code,
        TimeProvider clock) => evidence with
        {
            ResponseDigest = DiscoveryHash.Of(code),
            Outcome = outcome,
            ErrorCode = code,
            ReceivedAtUtc = evidence.StatusCode == 0 ? clock.GetUtcNow() : evidence.ReceivedAtUtc
        };

    /// <summary>Creates the JSON GET request with the existing no-metadata OData media contract.</summary>
    internal static HttpRequestMessage CreateGetRequest(Uri requestUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd(JsonAcceptMediaType);
        return request;
    }

    /// <summary>Resolves SDK file metadata and owning-item availability, preserving denial or projection failures as locator evidence.</summary>
    public async Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl,
        CancellationToken cancellationToken = default)
    {
        var stage = "FileRequest";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = await context.Web.GetFileByServerRelativeUrlOrDefaultAsync(serverRelativeUrl,
                item => item.UniqueId, item => item.Name, item => item.ServerRelativeUrl,
                item => item.CustomizedPageStatus, item => item.ListId,
                item => item.ListItemAllFields)
                .ConfigureAwait(false);
            if (file == null)
                return new(DiscoveryTerminalOutcome.Failed, null, null, serverRelativeUrl, null,
                    FileResolutionProvider, "locator_not_found",
                    ErrorStage: stage, ErrorDetail: "The SDK returned no physical file.");
            stage = "FileMetadataProjection";
            return MapResolvedFile(file);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var denied = AssessmentWebDiscovery.Classify(ex) == DiscoveryTerminalOutcome.Denied || IsDenied(ex);
            return new(denied ? DiscoveryTerminalOutcome.Denied : DiscoveryTerminalOutcome.Failed,
                null, null, serverRelativeUrl, null,
                FileResolutionProvider,
                denied ? "locator_denied" : "locator_resolution_failed",
                ErrorStage: stage, ErrorDetail: ResolutionErrorDetail(ex));
        }
    }

    /// <summary>Bounds persisted resolution errors and redacts recognizable credentials without storing authentication objects or stack traces.</summary>
    private static string ResolutionErrorDetail(Exception error)
    {
        // Persist the failure, not authentication objects, response headers, or stack traces.
        var detail = $"{error.GetType().FullName}: {AssessmentWebDiscovery.ErrorCode(error)}; {AssessmentWebDiscovery.ErrorDetail(error)}";
        detail = Regex.Replace(detail, @"\bBearer\s+\S+", "Bearer [redacted]", RegexOptions.IgnoreCase);
        detail = Regex.Replace(detail, @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", "[redacted-token]");
        detail = Regex.Replace(detail, @"(access_token|refresh_token|client_secret|password)\s*[:=]\s*[^&\s;]+", "$1=[redacted]", RegexOptions.IgnoreCase);
        detail = detail.Replace('\r', ' ').Replace('\n', ' ');
        return detail.Length > 1024 ? detail[..1024] : detail;
    }

    /// <summary>
    /// Projects a resolved <see cref="IFile"/> onto the discovery record, normalizing the owning list.
    /// </summary>
    /// <remarks>
    /// <c>IFile.ListId</c> is a non-nullable <see cref="Guid"/>, so a file that belongs to no list — the
    /// normal state of a web-root file — reports <see cref="Guid.Empty"/> rather than an absent value.
    /// Carrying that sentinel forward would make "this file has no owning list" indistinguishable from
    /// "this file names a list", and the assessment treats the latter as an unresolved lookup.
    /// </remarks>
    internal static SharePointResolvedFile MapResolvedFile(IFile file)
    {
        // A physical form/view can exist without a backing list item. The SDK returns an
        // unloaded model for that missing expansion, so ?.Id is not an availability check.
        Guid? listId = file.IsPropertyAvailable(value => value.ListId) && file.ListId != Guid.Empty
            ? file.ListId : null;
        var item = file.IsPropertyAvailable(value => value.ListItemAllFields) ? file.ListItemAllFields : null;
        int? itemId = item != null && item.IsPropertyAvailable(value => value.Id) && item.Id > 0
            ? item.Id : null;
        return new(DiscoveryTerminalOutcome.Complete, file.UniqueId.ToString("D"), file.Name,
            file.ServerRelativeUrl, file.CustomizedPageStatus.ToString(),
            FileResolutionProvider, null, listId, itemId, null);
    }

    /// <summary>Reads the SDK welcome-page property, preserving caller cancellation and explicit denial or failure provenance.</summary>
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

    /// <summary>Reads one SDK folder with requested child collections; missing expansions remain failures rather than empty-folder coverage.</summary>
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
                    file => file.ServerRelativeUrl, file => file.CustomizedPageStatus,
                    file => file.Properties)).ConfigureAwait(false);
            if (folder == null)
                return new(DiscoveryTerminalOutcome.Failed, null, serverRelativeUrl,
                    Array.Empty<SharePointModeledFolder>(), Array.Empty<SharePointModeledFile>(),
                    provider, operation, "folder_not_found", provider + ":" + operation,
                    DateTimeOffset.UtcNow);
            // An unexpanded collection materialises as zero items, which would be indistinguishable
            // from a genuinely empty folder and would claim coverage this call never observed.
            if (!folder.Folders.Requested || !folder.Files.Requested)
                return new(DiscoveryTerminalOutcome.Failed, folder.UniqueId.ToString("D"),
                    folder.ServerRelativeUrl, Array.Empty<SharePointModeledFolder>(),
                    Array.Empty<SharePointModeledFile>(), provider, operation,
                    "web_root_folder_not_expanded", provider + ":" + operation + ":NotExpanded",
                    DateTimeOffset.UtcNow);
            var folders = MapFolders(folder.Folders);
            var files = MapFiles(folder.Files);
            var outcome = folders.Count == 0 && files.Count == 0
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

    // PnP Core model collections implement IQueryable<T>, so a bare Select() binds to
    // Queryable.Select and is handed to the SDK query provider, which supports queries for its own
    // model type only and fails any projection with an InvalidCastException. AsRequested() exposes
    // the items the expand already returned, keeping the projection in memory.
    /// <summary>Projects only already-requested folder models in memory, avoiding unsupported SDK IQueryable projections.</summary>
    internal static IReadOnlyList<SharePointModeledFolder> MapFolders(IFolderCollection folders) =>
        folders.AsRequested().Select(child => new SharePointModeledFolder(
            child.UniqueId.ToString("D"), child.Name, child.ServerRelativeUrl)).ToArray();

    /// <summary>Projects already-requested file models and available setup-path metadata without issuing another SDK query.</summary>
    internal static IReadOnlyList<SharePointModeledFile> MapFiles(IFileCollection files) =>
        files.AsRequested().Select(file => new SharePointModeledFile(
            file.UniqueId.ToString("D"), file.Name, file.ServerRelativeUrl,
            file.CustomizedPageStatus.ToString(), SetupPath(file))).ToArray();

    /// <summary>Disposes the owned HTTP client and, only when ownership was granted, its SDK context.</summary>
    public void Dispose()
    {
        ownedJsonTransport.Dispose();
        if (ownsContext)
        {
            context.Dispose();
        }
    }

    // PnP Core normalizes the REST '_x005f_' escaping back to '_' when it fills a property bag,
    // so the modeled surface exposes the setup path under its unescaped vti_setuppath name.
    /// <summary>Reads the SDK-normalized setup-path property only when the file property bag was actually loaded.</summary>
    private static string SetupPath(IFile file) =>
        file.IsPropertyAvailable(item => item.Properties)
            ? file.Properties.GetString(SetupPathProperty, null) : null;

    /// <summary>Recognizes the existing SDK denial-message signals without converting unrelated failures to access-denied outcomes.</summary>
    private static bool IsDenied(Exception ex) =>
        ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts cloned rows and continuation links from supported OData and plain JSON envelope shapes.</summary>
    internal static (IReadOnlyList<JsonElement> Items, string NextLink, string SchemaFlavor) ParseEnvelope(
        JsonElement root)
    {
        if (TryProperty(root, JsonEnvelope.Value, out var value) && value.ValueKind == JsonValueKind.Array)
            return (value.EnumerateArray().Select(item => item.Clone()).ToArray(),
                String(root, JsonEnvelope.NextLink) ?? String(root, JsonEnvelope.LegacyNextLink), JsonEnvelope.NoMetadataFlavor);
        if (TryProperty(root, JsonEnvelope.VerboseData, out var verbose))
        {
            if (TryProperty(verbose, JsonEnvelope.Results, out var results) && results.ValueKind == JsonValueKind.Array)
                return (results.EnumerateArray().Select(item => item.Clone()).ToArray(),
                    String(verbose, JsonEnvelope.VerboseNext), JsonEnvelope.VerboseFlavor);
            return (new[] { verbose.Clone() }, String(verbose, JsonEnvelope.VerboseNext), JsonEnvelope.VerboseObjectFlavor);
        }
        if (root.ValueKind == JsonValueKind.Array)
            return (root.EnumerateArray().Select(item => item.Clone()).ToArray(), null, JsonEnvelope.ArrayFlavor);
        return (new[] { root.Clone() }, String(root, JsonEnvelope.NextLink) ?? String(root, JsonEnvelope.LegacyNextLink) ??
            String(root, JsonEnvelope.VerboseNext), JsonEnvelope.ObjectFlavor);
    }

    /// <summary>Finds an object property by ordinal case-insensitive name without assuming every JSON value is an object.</summary>
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

    /// <summary>Reads a non-null JSON property as text while preserving missing and null values as absent.</summary>
    internal static string String(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ToString() : null;

    /// <summary>Returns the first supported response or content-header value in the supplied precedence order.</summary>
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

    /// <summary>Trims and bounds header evidence, discarding blank values before persistence.</summary>
    private static string BoundedHeader(string value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, 256)];
    }
}
