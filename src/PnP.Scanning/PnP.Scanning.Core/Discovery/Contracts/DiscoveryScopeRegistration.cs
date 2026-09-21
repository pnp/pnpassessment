using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Discovery;

internal sealed record DiscoveryScopeRegistration(
    string ScopeKey,
    string ParentScopeKey,
    DiscoveryScopeKind Kind,
    DiscoverySourceKind? SourceKind,
    string Locator,
    string PermissionContext,
    bool Required = true,
    string ExclusionRuleId = null,
    string ExclusionRuleVersion = null,
    string ExclusionRuleHash = null,
    string ExclusionApprovalRef = null,
    IReadOnlyDictionary<string, string> Metadata = null);
