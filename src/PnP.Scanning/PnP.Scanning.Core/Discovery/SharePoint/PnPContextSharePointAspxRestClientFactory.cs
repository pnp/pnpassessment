using PnP.Core.Services;

namespace PnP.Scanning.Core.Discovery;

internal sealed class PnPContextSharePointAspxRestClientFactory : ISharePointAspxRestClientFactory
{
    private readonly IPnPContextFactory contextFactory;
    private readonly IAuthenticationProvider authenticationProvider;
    private readonly Guid? scanId;
    private readonly Func<PnPContext, HttpClient> jsonTransportFactory;
    private readonly Dictionary<string, ISharePointAspxRestClient> clients = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a per-web client cache, retaining caller ownership of any shared context or injected transport.</summary>
    internal PnPContextSharePointAspxRestClientFactory(IPnPContextFactory contextFactory,
        IAuthenticationProvider authenticationProvider, Guid? scanId = null, PnPContext sharedContext = null,
        Func<PnPContext, HttpClient> jsonTransportFactory = null)
    {
        this.contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        this.authenticationProvider = authenticationProvider ?? throw new ArgumentNullException(nameof(authenticationProvider));
        this.scanId = scanId;
        // Explicit borrowed transports retain caller ownership; no environment/test autodetection.
        this.jsonTransportFactory = jsonTransportFactory;
        if (sharedContext != null)
            clients.Add(sharedContext.Uri.AbsoluteUri.TrimEnd('/'),
                new PnPContextSharePointAspxRestClient(sharedContext, ownsContext: false,
                    jsonTransport: jsonTransportFactory?.Invoke(sharedContext)));
    }

    /// <summary>Reuses an exact cached web client or creates an authenticated scan-tagged context for that web.</summary>
    public async Task<ISharePointAspxRestClient> GetAsync(Uri webUrl,
        CancellationToken cancellationToken = default)
    {
        var key = webUrl.AbsoluteUri.TrimEnd('/');
        if (clients.TryGetValue(key, out var existing))
        {
            return existing;
        }
        var contextOptions = new PnPContextOptions();
        if (scanId.HasValue)
            contextOptions.Properties = new Dictionary<string, object> { [Constants.PnPContextPropertyScanId] = scanId.Value };
        var context = await contextFactory.CreateAsync(webUrl, authenticationProvider, cancellationToken,
            contextOptions).ConfigureAwait(false);
        var client = new PnPContextSharePointAspxRestClient(context,
            jsonTransport: jsonTransportFactory?.Invoke(context));
        clients.Add(key, client);
        return client;
    }

    /// <summary>Disposes cached client wrappers and their owned resources, leaving explicitly borrowed contexts and transports to their owners.</summary>
    public void Dispose()
    {
        foreach (var client in clients.Values)
        {
            client.Dispose();
        }
        clients.Clear();
    }
}
