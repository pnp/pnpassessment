namespace PnP.Scanning.Core.Discovery;

internal interface IAspxDiscoveryProvider : IDisposable
{
    DiscoveryScopeRegistration RootScope { get; }
    Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(
        DiscoveryScopeRegistration parent, CancellationToken cancellationToken = default);
    IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface);
}
