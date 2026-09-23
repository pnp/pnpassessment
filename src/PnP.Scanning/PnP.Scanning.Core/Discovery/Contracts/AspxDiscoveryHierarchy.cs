namespace PnP.Scanning.Core.Discovery;

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
