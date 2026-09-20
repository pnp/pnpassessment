namespace PnP.Scanning.Core.Discovery;

internal sealed record DiscoveryChildEnumerationResult(
    DiscoveryScopeKind ChildKind,
    IReadOnlyList<DiscoveryChildExpectation> ExpectedChildren,
    IReadOnlyList<DiscoveryScopeRegistration> ObservedChildren,
    DiscoveryTerminalOutcome Outcome,
    string PermissionContext,
    string GapCode = null,
    string GapDetail = null);

internal interface IAspxDiscoveryProvider : IDisposable
{
    DiscoveryScopeRegistration RootScope { get; }
    Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(
        DiscoveryScopeRegistration parent, CancellationToken cancellationToken = default);
    IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface);
}

internal interface IRawDiscoverySource
{
    IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync(CancellationToken cancellationToken = default);
}

internal static class AspxDiscoveryHierarchy
{
    internal static DiscoveryScopeKind ChildKindFor(DiscoveryScopeKind parentKind) => parentKind switch
    {
        DiscoveryScopeKind.Web => DiscoveryScopeKind.Container,
        DiscoveryScopeKind.Container => DiscoveryScopeKind.Folder,
        DiscoveryScopeKind.Folder => DiscoveryScopeKind.Folder,
        _ => throw new ArgumentOutOfRangeException(nameof(parentKind), parentKind, null),
    };
}
