#nullable enable
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
namespace PnP.Scanning.Core.Pipeline.Collection.Shared;

/// <summary>
/// Forwards requests through a shared SDK client with linked collection cancellation.
/// </summary>
internal sealed class CollectionHttpClient(HttpClient sharedClient, CancellationToken collectionToken) : HttpMessageHandler
{
    /// <summary>
    /// Forwards a cloned request through the shared client with linked cancellation and caller-owned response disposal.
    /// </summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(collectionToken, cancellationToken);
        using HttpRequestMessage copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (KeyValuePair<string, object?> option in request.Options) copy.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        if (request.Content != null)
        {
            copy.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(linked.Token));
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return await sharedClient.SendAsync(copy, HttpCompletionOption.ResponseHeadersRead, linked.Token);
    }
}
