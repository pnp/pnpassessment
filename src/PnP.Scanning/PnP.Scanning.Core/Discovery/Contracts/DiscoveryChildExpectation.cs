using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Discovery;

internal sealed record DiscoveryChildExpectation(
    string ScopeKey,
    DiscoveryScopeKind Kind,
    DiscoverySourceKind? SourceKind,
    string Locator,
    string PermissionContext,
    bool Required = true);
