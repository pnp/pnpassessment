using System.Security.Cryptography;
using System.Text;

namespace PnP.Scanning.Core.Discovery;

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
