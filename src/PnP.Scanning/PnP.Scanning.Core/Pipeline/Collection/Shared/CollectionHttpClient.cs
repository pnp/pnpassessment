namespace PnP.Scanning.Core.Pipeline.Collection.Shared;

/// <summary>CSOM's legacy executor has no SendAsync token parameter. Bind cancellation at the collection transport boundary.</summary>
internal sealed class CollectionHttpClient(HttpClient sharedClient, CancellationToken collectionToken) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(collectionToken, cancellationToken);
        using var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) copy.Options.Set(new HttpRequestOptionsKey<object>(option.Key), option.Value);
        if (request.Content != null)
        {
            copy.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(linked.Token));
            foreach (var header in request.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return await sharedClient.SendAsync(copy, HttpCompletionOption.ResponseHeadersRead, linked.Token);
    }
}
