namespace PnP.Scanning.Core.Discovery;

internal sealed record DiscoveryChildEnumerationResult(
    DiscoveryScopeKind ChildKind,
    IReadOnlyList<DiscoveryChildExpectation> ExpectedChildren,
    IReadOnlyList<DiscoveryScopeRegistration> ObservedChildren,
    DiscoveryTerminalOutcome Outcome,
    string PermissionContext,
    string GapCode = null,
    string GapDetail = null);
