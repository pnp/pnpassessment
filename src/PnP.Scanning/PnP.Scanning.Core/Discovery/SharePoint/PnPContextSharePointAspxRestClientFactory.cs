using PnP.Core.Services;

namespace PnP.Scanning.Core.Discovery;

internal sealed class PnPContextSharePointAspxRestClientFactory : ISharePointAspxRestClientFactory
{
    private readonly IPnPContextFactory contextFactory;
    private readonly IAuthenticationProvider authenticationProvider;
    private readonly Guid? scanId;
    private readonly Dictionary<string, ISharePointAspxRestClient> clients = new(StringComparer.OrdinalIgnoreCase);

    internal PnPContextSharePointAspxRestClientFactory(IPnPContextFactory contextFactory,
        IAuthenticationProvider authenticationProvider, Guid? scanId = null, bool discoveryTestTraffic = false)
    {
        this.contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        ArgumentNullException.ThrowIfNull(authenticationProvider);
        this.authenticationProvider = DiscoveryTestTrafficHandler.ForDiscovery(authenticationProvider, discoveryTestTraffic);
        this.scanId = scanId;
    }

    public async Task<ISharePointAspxRestClient> GetAsync(Uri webUrl,
        CancellationToken cancellationToken = default)
    {
        var key = webUrl.AbsoluteUri.TrimEnd('/');
        if (clients.TryGetValue(key, out var existing)) return existing;
        var context = await CreateContextAsync(webUrl, cancellationToken).ConfigureAwait(false);
        var client = new PnPContextSharePointAspxRestClient(context);
        clients.Add(key, client);
        return client;
    }

    internal async Task<PnPContext> CreateContextAsync(Uri webUrl, CancellationToken cancellationToken)
    {
        var contextOptions = new PnPContextOptions();
        if (scanId.HasValue)
            contextOptions.Properties = new Dictionary<string, object> { [Constants.PnPContextPropertyScanId] = scanId.Value };
        return await contextFactory.CreateAsync(webUrl, authenticationProvider, cancellationToken,
            contextOptions).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var client in clients.Values) client.Dispose();
        clients.Clear();
    }
}
