using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal enum DiscoveryScopeKind { Tenant, Geo, SiteCollection, Web, Container, Folder }
internal enum DiscoverySourceKind { RawListLibraryFiles, WebRootFiles, ListFormBackingFiles, ListViewBackingFiles, TenantManifest }
public enum DiscoveryTerminalOutcome { Pending, Complete, Empty, PolicyExcluded, Denied, Failed, Truncated, Cancelled, Unknown }
internal enum DiscoveryAttemptStatus { Running, Interrupted, Failed, Superseded, Complete }
internal enum DiscoveryExecutionStatus { Running, Finished, Failed, Cancelled }
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
    internal const string BatchReplayConflict = "batch_replay_conflict";

    internal static bool ForcesUnknown(string code) => code is
        SourceUnsupported or SupplementSourceUnverified or PermissionVisibilityUnknown or
        FilenameMissing or IdentityMissing or MetadataConflict or LocatorIdentityConflict or
        ChangedDuringScan or PaginationTokenLoopOrLoss or BatchReplayConflict;
}

internal sealed record DiscoveryRunManifest(
    string ProductRef,
    string SdkRef,
    string ContractVersion,
    string SchemaVersion,
    string MigrationSetHash,
    string InputManifestHash,
    string ScopePolicyHash,
    string TenantManifestHash,
    string BinaryArtifactHash,
    string BuildManifestHash,
    string DependencyManifestHash,
    string EnvironmentManifestHash,
    string FixtureRevision = null,
    string FixtureHash = null)
{
    internal const string CurrentContractVersion = "aspx-discovery/v1";
    internal const string CurrentSchemaVersion = "aspx-discovery-sqlite/v1";

    internal IReadOnlyList<string> Validate(bool fixtureRun)
    {
        var missing = new List<string>();
        AddIfMissing(ProductRef, nameof(ProductRef), missing);
        AddIfMissing(SdkRef, nameof(SdkRef), missing);
        AddIfMissing(ContractVersion, nameof(ContractVersion), missing);
        AddIfMissing(SchemaVersion, nameof(SchemaVersion), missing);
        AddHashIfInvalid(MigrationSetHash, nameof(MigrationSetHash), missing);
        AddHashIfInvalid(InputManifestHash, nameof(InputManifestHash), missing);
        AddHashIfInvalid(ScopePolicyHash, nameof(ScopePolicyHash), missing);
        AddHashIfInvalid(TenantManifestHash, nameof(TenantManifestHash), missing);
        AddHashIfInvalid(BinaryArtifactHash, nameof(BinaryArtifactHash), missing);
        AddHashIfInvalid(BuildManifestHash, nameof(BuildManifestHash), missing);
        AddHashIfInvalid(DependencyManifestHash, nameof(DependencyManifestHash), missing);
        AddHashIfInvalid(EnvironmentManifestHash, nameof(EnvironmentManifestHash), missing);

        if (!IsImmutableGitRef(ProductRef)) missing.Add(nameof(ProductRef) + ":immutable-full-sha");
        if (!IsFullSha(SdkRef)) missing.Add(nameof(SdkRef) + ":immutable-full-sha");
        if (fixtureRun)
        {
            AddIfMissing(FixtureRevision, nameof(FixtureRevision), missing);
            AddHashIfInvalid(FixtureHash, nameof(FixtureHash), missing);
        }
        return missing;
    }

    internal string CanonicalJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });

    internal IReadOnlyList<string> Diff(DiscoveryRunManifest other) => GetType().GetProperties()
        .Where(property => !Equals(property.GetValue(this), property.GetValue(other)))
        .Select(property => property.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();

    private static bool IsImmutableGitRef(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.LastIndexOf('@');
        return separator > 0 && IsFullSha(value[(separator + 1)..]);
    }

    private static bool IsFullSha(string value) => value?.Length == 40 && value.All(Uri.IsHexDigit);

    private static void AddIfMissing(string value, string name, ICollection<string> missing)
    {
        if (string.IsNullOrWhiteSpace(value)) missing.Add(name);
    }

    private static void AddHashIfInvalid(string value, string name, ICollection<string> missing)
    {
        if (value?.Length != 64 || !value.All(Uri.IsHexDigit) ||
            !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            missing.Add(name + ":sha256");
        }
    }
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
    string ExclusionApprovalRef = null);

internal sealed record RawDiscoveryRecord(
    string NativeObjectId,
    string FileUniqueId,
    string ContainerStableId,
    string FileName,
    string PhysicalLocator,
    bool LocatorIsGuaranteedPhysicalFilePath,
    string PermissionContext,
    IReadOnlyDictionary<string, string> Metadata = null);

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

internal sealed record DiscoveryCounts(
    int ObservedCount, int EmittedCount, int InventoryCount, int BatchCount,
    int AttemptCount, int GapCount, int ConflictCount);

internal sealed record ResumeDecision(bool CanResume, IReadOnlyList<string> MismatchFields)
{
    internal static ResumeDecision Evaluate(DiscoveryRunManifest existing, DiscoveryRunManifest candidate, bool fixtureRun)
    {
        var invalid = existing.Validate(fixtureRun).Concat(candidate.Validate(fixtureRun))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (invalid.Length > 0) return new ResumeDecision(false, invalid);
        var differences = existing.Diff(candidate);
        return new ResumeDecision(differences.Count == 0, differences);
    }
}

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

    internal static string Metadata(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata == null || metadata.Count == 0) return "{}";
        return JsonSerializer.Serialize(metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
}
