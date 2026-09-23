using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using System.Net;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

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
                item => item.CustomizedPageStatus, item => item.ListId,
                item => item.ListItemAllFields)
                .ConfigureAwait(false);
            if (file == null)
                return new(DiscoveryTerminalOutcome.Failed, null, null, serverRelativeUrl, null,
                    "PnP.Core:IWeb.GetFileByServerRelativeUrlOrDefaultAsync", "locator_not_found");
            return new(DiscoveryTerminalOutcome.Complete, file.UniqueId.ToString("D"), file.Name,
                file.ServerRelativeUrl, file.CustomizedPageStatus.ToString(),
                "PnP.Core:IWeb.GetFileByServerRelativeUrlOrDefaultAsync", null,
                file.ListId, file.ListItemAllFields?.Id, null);
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
