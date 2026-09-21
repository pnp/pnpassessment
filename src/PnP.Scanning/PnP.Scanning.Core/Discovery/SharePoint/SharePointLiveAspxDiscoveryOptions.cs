namespace PnP.Scanning.Core.Discovery;

internal sealed record SharePointLiveAspxDiscoveryOptions(
    string PermissionContext,
    string VisibilityBoundary,
    string AuthorityRevision,
    string AuthorityHash,
    string PlatformBuildRef = null,
    AspxDiscoveryIntent Intent = AspxDiscoveryIntent.FullInventory);
