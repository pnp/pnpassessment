using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Discovery;

internal enum DiscoveryScopeKind { Web, Container, Folder }
internal enum DiscoverySourceKind { RawListLibraryFiles, WebRootFiles, ListFormBackingFiles, ListViewBackingFiles, WebWelcomePage }
internal enum AspxDiscoveryIntent { FullInventory, HomePageOnly }
public enum DiscoveryTerminalOutcome { Pending, Complete, Empty, PolicyExcluded, Denied, Failed, Truncated, Cancelled, Unknown }
public enum DiscoveryVerdict { CompleteTenantVerified, CompleteAuthorizedSurface, CompleteDeclaredSubset, Incomplete, Unknown }

internal static class DiscoveryGapCodes
{
    internal const string SourceUnsupported = "source_unsupported";
    internal const string SupplementSourceUnverified = "supplement_source_unverified";
    internal const string PermissionVisibilityUnknown = "permission_visibility_unknown";
    internal const string FilenameMissing = "filename_missing";
    internal const string IdentityMissing = "identity_missing";
    internal const string MetadataConflict = "metadata_conflict";
    internal const string LocatorIdentityConflict = "locator_identity_conflict";
    internal const string ChangedDuringScan = "changed_during_scan";
    internal const string PaginationTokenLoopOrLoss = "pagination_token_loop_or_loss";
    internal const string ExpectedChildMissing = "expected_child_missing";
    internal const string DenominatorDrift = "denominator_drift";
    internal const string BatchReplayConflict = "batch_replay_conflict";
    internal const string ScopeEnumerationFailed = "scope_enumeration_failed";
    internal const string ScopeExecutionFailed = "scope_execution_failed";
    internal const string LegacyVersionIncompatible = "legacy_version_incompatible";

    internal static bool ForcesUnknown(string code) => code is
        SourceUnsupported or SupplementSourceUnverified or PermissionVisibilityUnknown or
        FilenameMissing or IdentityMissing or MetadataConflict or LocatorIdentityConflict or
        ChangedDuringScan or PaginationTokenLoopOrLoss or DenominatorDrift or BatchReplayConflict or
        ScopeEnumerationFailed or ScopeExecutionFailed or LegacyVersionIncompatible;
}

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

internal sealed record DiscoveryChildExpectation(
    string ScopeKey,
    DiscoveryScopeKind Kind,
    DiscoverySourceKind? SourceKind,
    string Locator,
    string PermissionContext,
    bool Required = true);

internal sealed record RawDiscoveryRecord(
    string SourceObjectId,
    string FileUniqueId,
    string ContainerStableId,
    string FileName,
    string PhysicalLocator,
    bool LocatorIsGuaranteedPhysicalFilePath,
    string PermissionContext,
    IReadOnlyDictionary<string, string> Metadata = null,
    Guid? SiteCollectionId = null,
    Guid? WebId = null,
    Guid? ListId = null,
    Guid? FolderUniqueId = null,
    int? ListItemId = null,
    bool? HomePage = null,
    string ContentTypeId = null,
    string PageType = null,
    bool? LibraryHidden = null,
    string CustomizedPageStatusRaw = null,
    string ObservationMethod = null,
    string SelectionState = null,
    string WelcomePageStatus = null,
    string ScanId = null,
    string SiteUrl = null,
    string WebUrl = null);

internal sealed record RawDiscoveryBatch(
    int BatchOrdinal,
    string RequestFingerprint,
    string ResponseFingerprint,
    IReadOnlyList<RawDiscoveryRecord> Records,
    bool IsTerminal,
    DiscoveryTerminalOutcome TerminalOutcome,
    string NextCheckpoint = null,
    string GapCode = null,
    string GapDetail = null);

internal static class DiscoveryHash
{
    internal static string Of(params string[] values)
    {
        using var sha = SHA256.Create();
        var canonical = string.Join("|", values.Select(value =>
        {
            value ??= string.Empty;
            return value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
        }));
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
