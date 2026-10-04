using Microsoft.Extensions.DependencyInjection;
using PnP.Core.Services;

namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// Marks only requests opted in by a discovery context. HttpClient has already applied
/// PnP Core's real default headers here; never change shared client or global defaults.
/// </summary>
internal sealed class DiscoveryTestTrafficHandler : DelegatingHandler
{
    internal const string Marker = "testtraffic-smr";
    private static readonly HttpRequestOptionsKey<bool> Enabled = new("PnP.Scanning.DiscoveryTestTraffic");

    internal static void Register(IServiceCollection services)
    {
        services.AddHttpClient<SharePointRestClient>()
            .AddHttpMessageHandler(() => new DiscoveryTestTrafficHandler());
        services.AddHttpClient<MicrosoftGraphClient>()
            .AddHttpMessageHandler(() => new DiscoveryTestTrafficHandler());
    }

    internal static IAuthenticationProvider ForDiscovery(IAuthenticationProvider authenticationProvider, bool enabled) =>
        enabled ? new DiscoveryAuthenticationProvider(authenticationProvider) : authenticationProvider;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(Enabled, out var enabled) && enabled &&
            !request.Headers.UserAgent.Any(value => value.Product?.Name == Marker))
        {
            request.Headers.UserAgent.ParseAdd(Marker);
        }
        return base.SendAsync(request, cancellationToken);
    }

    // Authentication is the SDK's per-context hook shared by initialization, batches,
    // modeled reads and our direct REST requests. Carry intent, not a header override,
    // across that hook so HttpClient can still supply its default User-Agent.
    private sealed class DiscoveryAuthenticationProvider(IAuthenticationProvider inner) : IAuthenticationProvider
    {
        public async Task AuthenticateRequestAsync(Uri resource, HttpRequestMessage request)
        {
            await inner.AuthenticateRequestAsync(resource, request).ConfigureAwait(false);
            request.Options.Set(Enabled, true);
        }

        public Task<string> GetAccessTokenAsync(Uri resource, string[] scopes) => inner.GetAccessTokenAsync(resource, scopes);

        public Task<string> GetAccessTokenAsync(Uri resource) => inner.GetAccessTokenAsync(resource);
    }
}
