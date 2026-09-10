using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal static class AspxAcquisitionVersions
{
    internal const string SurfaceContract = "aspx-surface-applicability-denominator/v3";
    internal const string ReferenceProducer = "aspx-reference/v1";
    internal const string ReferenceStore = "aspx-reference-sqlite/v1";
    internal const string ReferenceOutput = "aspx-reference-output/v1";
    internal const string AggregateOutput = "aspx-acquisition-verdict/v1";
    internal const string LiveProvider = "sharepoint-live-aspx-provider/v1";
    internal const string Registry = "aspx-platform-registry/v1";
}

internal static class AspxReferenceSourceKinds
{
    internal const string ListForm = "ListFormReference";
    internal const string ListView = "ListViewReference";
    internal const string WebWelcomePage = "WebWelcomePageReference";
    internal const string PlatformRegistry = "PlatformRegistryReference";
    internal const string RuntimeRequest = "RuntimeRequestReference";

    internal static bool IsKnown(string value) => value is ListForm or ListView or WebWelcomePage or
        PlatformRegistry or RuntimeRequest;
}

internal static class AspxReferenceDispositions
{
    internal const string ReferenceOnlyAvailable = "ReferenceOnlyAvailable";
    internal const string ReferenceUnavailable = "ReferenceUnavailable";
    internal const string LinkedPhysicalGhosted = "LinkedPhysicalGhosted";
    internal const string LinkedPhysicalCustomized = "LinkedPhysicalCustomized";
    internal const string VirtualHandler = "VirtualHandler";
    internal const string NonAspx = "NonAspx";
    internal const string Unknown = "Unknown";

    internal static bool IsKnown(string value) => value is ReferenceOnlyAvailable or ReferenceUnavailable or
        LinkedPhysicalGhosted or LinkedPhysicalCustomized or VirtualHandler or NonAspx or Unknown;
}

internal enum AspxExpectedCountState { Known, Unknown }
internal enum AspxAggregateVerdict { CompleteAuthorizedSurface, Incomplete, Unknown }
internal enum AspxSurfaceApplicability { Applicable, SystemOrVirtualOnly, NotApplicable, Unknown }
internal enum AspxRuntimeCounterexampleState { NoneObserved, Observed, Unknown }

internal sealed record AspxListAdapterDecision(
    AspxSurfaceApplicability Applicability,
    bool RawLibraryRequired,
    bool FormsAndViewsRequired,
    AspxRuntimeCounterexampleState RuntimeCounterexampleState,
    DiscoveryTerminalOutcome Outcome);

internal static class AspxListApplicabilityPolicy
{
    internal static AspxListAdapterDecision Evaluate(int? actualBaseType)
    {
        if (actualBaseType == 1)
            return new(AspxSurfaceApplicability.Applicable, true, true,
                AspxRuntimeCounterexampleState.NoneObserved, DiscoveryTerminalOutcome.Complete);
        if (actualBaseType is 0 or 3 or 4 or 5)
            return new(AspxSurfaceApplicability.SystemOrVirtualOnly, false, true,
                AspxRuntimeCounterexampleState.NoneObserved, DiscoveryTerminalOutcome.Complete);
        if (actualBaseType == 2)
            return new(AspxSurfaceApplicability.NotApplicable, false, true,
                AspxRuntimeCounterexampleState.Observed, DiscoveryTerminalOutcome.Unknown);
        return new(AspxSurfaceApplicability.Unknown, false, true,
            actualBaseType == null ? AspxRuntimeCounterexampleState.Unknown : AspxRuntimeCounterexampleState.NoneObserved,
            DiscoveryTerminalOutcome.Unknown);
    }
}

internal sealed record AspxReferenceRunManifest(
    string ContractVersion,
    string SchemaVersion,
    string ProductRef,
    string SdkRef,
    string ScopeAuthorityHash,
    string PermissionBoundaryHash,
    string RegistryRevision,
    string RegistryHash,
    string PlatformBuildRef,
    string SnapshotFence,
    string ProviderVersion,
    string ArtifactRunId)
{
    internal IReadOnlyList<string> Validate()
    {
        var invalid = new List<string>();
        Require(ContractVersion == AspxAcquisitionVersions.ReferenceProducer, nameof(ContractVersion), invalid);
        Require(SchemaVersion == AspxAcquisitionVersions.ReferenceStore, nameof(SchemaVersion), invalid);
        Require(IsProductRef(ProductRef), nameof(ProductRef), invalid);
        Require(IsFullSha(SdkRef), nameof(SdkRef), invalid);
        Require(IsHash(ScopeAuthorityHash), nameof(ScopeAuthorityHash), invalid);
        Require(IsHash(PermissionBoundaryHash), nameof(PermissionBoundaryHash), invalid);
        Require(!string.IsNullOrWhiteSpace(RegistryRevision), nameof(RegistryRevision), invalid);
        Require(IsHash(RegistryHash), nameof(RegistryHash), invalid);
        Require(!string.IsNullOrWhiteSpace(PlatformBuildRef), nameof(PlatformBuildRef), invalid);
        Require(!string.IsNullOrWhiteSpace(SnapshotFence), nameof(SnapshotFence), invalid);
        Require(ProviderVersion == AspxAcquisitionVersions.LiveProvider, nameof(ProviderVersion), invalid);
        Require(Guid.TryParse(ArtifactRunId, out _), nameof(ArtifactRunId), invalid);
        return invalid;
    }

    internal string CanonicalJson() => JsonSerializer.Serialize(this, AspxInventoryRuntime.JsonOptions());

    internal string Hash() => DiscoveryHash.Of(CanonicalJson());

    private static bool IsProductRef(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.LastIndexOf('@');
        return separator > 0 && IsFullSha(value[(separator + 1)..]);
    }

    private static bool IsFullSha(string value) => value?.Length == 40 && value.All(Uri.IsHexDigit);
    private static bool IsHash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit) &&
        string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);
    private static void Require(bool condition, string field, ICollection<string> invalid)
    {
        if (!condition) invalid.Add(field);
    }
}

internal sealed record AspxPlatformRegistryV1(
    string RegistrySchemaVersion,
    string RegistryRevision,
    string RegistryHash,
    string AuthorityKind,
    string AuthoritySourceRef,
    string AuthorityArtifactHash,
    string ReviewRef,
    DateTimeOffset GeneratedAtUtc,
    string PlatformFamily,
    string PlatformBuildMin,
    string PlatformBuildMax,
    IReadOnlyList<AspxPlatformRegistryEntry> Entries)
{
    internal bool IsCompatible(string platformBuildRef) =>
        RegistrySchemaVersion == AspxAcquisitionVersions.Registry &&
        !string.IsNullOrWhiteSpace(RegistryRevision) && IsHash(RegistryHash) &&
        !string.IsNullOrWhiteSpace(AuthorityKind) && !string.IsNullOrWhiteSpace(AuthoritySourceRef) &&
        IsHash(AuthorityArtifactHash) && !string.IsNullOrWhiteSpace(ReviewRef) &&
        !string.IsNullOrWhiteSpace(platformBuildRef) &&
        CompareBuild(platformBuildRef, PlatformBuildMin) >= 0 && CompareBuild(platformBuildRef, PlatformBuildMax) <= 0;

    internal IReadOnlyList<string> Validate(string platformBuildRef)
    {
        var invalid = new List<string>();
        if (!IsCompatible(platformBuildRef)) invalid.Add("registry_or_platform_binding");
        var collisions = (Entries ?? Array.Empty<AspxPlatformRegistryEntry>())
            .SelectMany(entry => new[] { entry.CanonicalRequestPath }.Concat(entry.Aliases ?? Array.Empty<string>())
                .Select(path => (Path: NormalizeRequestPath(path), Entry: entry.ReferenceId)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .GroupBy(item => item.Path, StringComparer.Ordinal)
            .Where(group => group.Select(item => item.Entry).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key).ToArray();
        if (collisions.Length > 0) invalid.Add("registry_alias_collision:" + string.Join(',', collisions));
        return invalid;
    }

    internal static string NormalizeRequestPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized.ToLowerInvariant();
    }

    private static int CompareBuild(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return int.MinValue;
        if (right == "*") return 0;
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
            return leftVersion.CompareTo(rightVersion);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit) &&
        string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);
}

internal sealed record AspxPlatformRegistryEntry(
    string ReferenceId,
    string SurfaceKind,
    string CanonicalRequestPath,
    IReadOnlyList<string> Aliases,
    string HandlerOrArtifactType,
    string ApplicabilityRuleId,
    string ApplicabilityRuleHash,
    string ExpectedAvailability,
    string DownstreamDisposition);

internal sealed record AspxReviewedNotApplicableRule(
    string RuleId,
    string RuleVersion,
    string RuleHash,
    string ReviewRef,
    string ApprovalRef,
    string PlatformBuildMin,
    string PlatformBuildMax,
    string AuthorityRevision,
    string AuthorityHash,
    AspxRuntimeCounterexampleState RuntimeCounterexampleState)
{
    internal bool IsValid(string platformBuildRef) =>
        !string.IsNullOrWhiteSpace(RuleId) && !string.IsNullOrWhiteSpace(RuleVersion) &&
        IsHash(RuleHash) && !string.IsNullOrWhiteSpace(ReviewRef) && !string.IsNullOrWhiteSpace(ApprovalRef) &&
        IsHash(AuthorityHash) && !string.IsNullOrWhiteSpace(AuthorityRevision) &&
        RuntimeCounterexampleState == AspxRuntimeCounterexampleState.NoneObserved &&
        AspxPlatformRegistryV1BuildRange.Contains(platformBuildRef, PlatformBuildMin, PlatformBuildMax);

    internal bool Matches(string ruleId, string ruleVersion, string ruleHash, string authorityRevision,
        string platformBuildRef) => IsValid(platformBuildRef) &&
        string.Equals(RuleId, ruleId, StringComparison.Ordinal) &&
        string.Equals(RuleVersion, ruleVersion, StringComparison.Ordinal) &&
        string.Equals(RuleHash, ruleHash, StringComparison.Ordinal) &&
        string.Equals(AuthorityRevision, authorityRevision, StringComparison.Ordinal);

    private static bool IsHash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit) &&
        string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);
}

internal static class AspxPlatformRegistryV1BuildRange
{
    internal static bool Contains(string value, string minimum, string maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(minimum) || string.IsNullOrWhiteSpace(maximum))
            return false;
        if (minimum == "*" && maximum == "*") return true;
        if (Version.TryParse(value, out var parsed) && Version.TryParse(minimum, out var min) &&
            Version.TryParse(maximum, out var max)) return parsed >= min && parsed <= max;
        return string.Compare(value, minimum, StringComparison.OrdinalIgnoreCase) >= 0 &&
            string.Compare(value, maximum, StringComparison.OrdinalIgnoreCase) <= 0;
    }
}

internal sealed record AspxPaginationPageReceipt(
    string CollectionScopeKey,
    string AuthorityRevision,
    string ActualEndpointHash,
    int PageOrdinal,
    string RequestTokenHash,
    int ResponseItemCount,
    string NextTokenHash,
    string ResponseDigest,
    bool TerminalFlag,
    DateTimeOffset ReceivedAtUtc);

internal sealed record AspxPaginationValidation(
    DiscoveryTerminalOutcome Outcome,
    string ChainHash,
    int OutstandingTokenCount,
    IReadOnlyList<string> GapCodes);

internal static class AspxPaginationContract
{
    internal static AspxPaginationValidation Validate(IReadOnlyList<AspxPaginationPageReceipt> pages,
        DiscoveryTerminalOutcome providerOutcome)
    {
        pages ??= Array.Empty<AspxPaginationPageReceipt>();
        var gaps = new List<string>();
        var outstanding = new HashSet<string>(StringComparer.Ordinal);
        string endpoint = null;
        string nextExpectedRequest = null;

        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            if (page.PageOrdinal != index) gaps.Add("pagination_ordinal_integrity");
            endpoint ??= page.ActualEndpointHash;
            if (!string.Equals(endpoint, page.ActualEndpointHash, StringComparison.Ordinal))
                gaps.Add("pagination_endpoint_drift");
            if (!string.Equals(nextExpectedRequest, page.RequestTokenHash, StringComparison.Ordinal))
                gaps.Add("pagination_token_binding");
            if (!string.IsNullOrWhiteSpace(page.RequestTokenHash)) outstanding.Remove(page.RequestTokenHash);
            if (!string.IsNullOrWhiteSpace(page.NextTokenHash))
            {
                if (!outstanding.Add(page.NextTokenHash)) gaps.Add("pagination_token_loop_or_loss");
                if (page.TerminalFlag) gaps.Add("terminal_page_returned_token");
            }
            nextExpectedRequest = page.NextTokenHash;
            if (page.TerminalFlag && index != pages.Count - 1) gaps.Add("pagination_after_terminal");
        }

        if (pages.Count == 0) gaps.Add("pagination_receipt_missing");
        else if (!pages[^1].TerminalFlag) gaps.Add("pagination_terminal_missing");
        if (outstanding.Count > 0) gaps.Add("pagination_outstanding_token");

        var canonical = string.Join("\n", pages.Select(page => string.Join('|', page.CollectionScopeKey,
            page.AuthorityRevision, page.ActualEndpointHash, page.PageOrdinal, page.RequestTokenHash,
            page.ResponseItemCount, page.NextTokenHash, page.ResponseDigest, page.TerminalFlag)));
        var outcome = gaps.Count == 0 ? providerOutcome : DiscoveryTerminalOutcome.Unknown;
        return new(outcome, DiscoveryHash.Of(canonical), outstanding.Count,
            gaps.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    internal static string TokenHash(string token) => string.IsNullOrWhiteSpace(token) ? null : DiscoveryHash.Of(token);
}

internal sealed record AspxSurfaceDenominatorRow(
    string SurfaceContractVersion,
    Guid AcquisitionRunId,
    string SnapshotFence,
    string ScopeAuthorityHash,
    string ScopeKey,
    string ParentScopeKey,
    string SurfaceId,
    AspxSurfaceApplicability Applicability,
    string ApplicabilityRuleId,
    string ApplicabilityRuleVersion,
    string ApplicabilityRuleHash,
    string ApplicabilityReviewRef,
    string ApplicabilityApprovalRef,
    string ApplicabilityPlatformBinding,
    AspxRuntimeCounterexampleState RuntimeCounterexampleState,
    string AuthorityKind,
    string AuthorityLocator,
    string AuthorityRevision,
    string AuthorityHash,
    string ActualMethod,
    string ActualEndpoint,
    string ActualSelect,
    string ActualFilter,
    string VisibilityBoundary,
    string PermissionContext,
    int? ExpectedCount,
    AspxExpectedCountState ExpectedCountState,
    int ObservedCount,
    DiscoveryTerminalOutcome TerminalOutcome,
    string AggregateEffect,
    bool ContinuationRemaining,
    string PaginationChainHash,
    int PaginationOutstandingTokenCount,
    string AbsenceProofKind,
    string AbsenceProofRef,
    string ProviderVersion,
    string ProductRef,
    string SdkRef,
    string PlatformBuildRef,
    string RegistryRevision,
    string RegistryHash,
    string ArtifactRunId,
    DateTimeOffset AsOfUtc,
    IReadOnlyList<string> EvidenceRefs,
    string RequiredAdapter,
    string ClassificationEffect)
{
    internal IReadOnlyList<string> Validate(AspxReviewedNotApplicableRule rule = null)
    {
        var invalid = new List<string>();
        if (ExpectedCountState == AspxExpectedCountState.Unknown && ExpectedCount != null)
            invalid.Add("unknown_expected_count_must_be_null");
        if (ExpectedCountState == AspxExpectedCountState.Known && ExpectedCount == null)
            invalid.Add("known_expected_count_required");
        if (TerminalOutcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty)
        {
            if (ContinuationRemaining || PaginationOutstandingTokenCount != 0)
                invalid.Add("terminal_with_outstanding_pagination");
        }
        if (Applicability == AspxSurfaceApplicability.NotApplicable &&
            (rule == null || !rule.IsValid(PlatformBuildRef))) invalid.Add("invalid_not_applicable_rule");
        return invalid;
    }
}

internal sealed record AspxReferenceObservation(
    string ReferenceObservationId,
    string RecordKind,
    string SourceKind,
    string SourceObjectId,
    string AcquisitionMethod,
    string ReferenceId,
    string RawLocator,
    string CanonicalRequestPath,
    string MatchedAlias,
    string PlatformBuildRef,
    string RegistryRevision,
    string RegistryHash,
    string Disposition,
    string ReasonCode,
    string LinkedPhysicalCanonicalInventoryKey,
    string LinkedFileUniqueId,
    string ContentOrigin,
    string PermissionContext,
    IReadOnlyList<string> EvidenceRefs)
{
    internal const string RequiredRecordKind = "AspxReferenceObservation";

    internal IReadOnlyList<string> Validate()
    {
        var invalid = new List<string>();
        if (RecordKind != RequiredRecordKind) invalid.Add("record_kind");
        if (!AspxReferenceSourceKinds.IsKnown(SourceKind)) invalid.Add("unknown_source_kind");
        if (!AspxReferenceDispositions.IsKnown(Disposition)) invalid.Add("unknown_disposition");
        var physicalForbidden = Disposition is AspxReferenceDispositions.ReferenceOnlyAvailable or
            AspxReferenceDispositions.ReferenceUnavailable or AspxReferenceDispositions.VirtualHandler or
            AspxReferenceDispositions.NonAspx;
        if (physicalForbidden && (!string.IsNullOrWhiteSpace(LinkedPhysicalCanonicalInventoryKey) ||
            !string.IsNullOrWhiteSpace(LinkedFileUniqueId))) invalid.Add("non_physical_disposition_has_physical_identity");
        var physicalRequired = Disposition is AspxReferenceDispositions.LinkedPhysicalGhosted or
            AspxReferenceDispositions.LinkedPhysicalCustomized;
        if (physicalRequired && (string.IsNullOrWhiteSpace(LinkedPhysicalCanonicalInventoryKey) ||
            string.IsNullOrWhiteSpace(LinkedFileUniqueId))) invalid.Add("linked_physical_identity_missing");
        if (Disposition == AspxReferenceDispositions.LinkedPhysicalGhosted &&
            !string.Equals(ContentOrigin, "verified-ghosted", StringComparison.Ordinal))
            invalid.Add("ghosted_origin_unverified");
        return invalid;
    }
}

internal sealed record AspxReferenceOutputV1(
    string OutputVersion,
    Guid AcquisitionRunId,
    string ManifestHash,
    AspxAggregateVerdict CoverageVerdict,
    IReadOnlyList<AspxReferenceObservation> References,
    IReadOnlyList<AspxSurfaceDenominatorRow> Denominator,
    IReadOnlyList<AspxPaginationPageReceipt> PaginationReceipts,
    IReadOnlyList<string> GapCodes)
{
    internal const string Version = AspxAcquisitionVersions.ReferenceOutput;
}

internal sealed record AspxVolumeBinding(
    string OutputVersion,
    Guid RunId,
    string Sha256,
    long Length,
    string ProductRef,
    string ScopeAuthorityHash,
    string SnapshotFence);

internal sealed record AspxAcquisitionVerdictV1(
    string OutputVersion,
    Guid AcquisitionRunId,
    AspxAggregateVerdict AggregateVerdict,
    AspxVolumeBinding PhysicalVolume,
    AspxVolumeBinding ReferenceVolume,
    string SurfaceContractVersion,
    string RegistryRevision,
    string RegistryHash,
    string PlatformBuildRef,
    string ProductRef,
    string SdkRef,
    DateTimeOffset SealedAtUtc,
    IReadOnlyList<string> GapCodes)
{
    internal const string Version = AspxAcquisitionVersions.AggregateOutput;
}

internal static class AspxAggregateEvaluator
{
    internal static AspxAggregateVerdict Evaluate(AspxDiscoveryOutputV2 physical, AspxReferenceOutputV1 reference,
        AspxReferenceRunManifest manifest, AspxPlatformRegistryV1 registry, out IReadOnlyList<string> gaps)
    {
        var found = new HashSet<string>(reference.GapCodes ?? Array.Empty<string>(), StringComparer.Ordinal);
        foreach (var invalid in manifest.Validate()) found.Add("reference_manifest:" + invalid);
        foreach (var invalid in registry.Validate(manifest.PlatformBuildRef)) found.Add("registry:" + invalid);
        if (!string.Equals(manifest.RegistryRevision, registry.RegistryRevision, StringComparison.Ordinal))
            found.Add("registry:revision_drift");
        if (!string.Equals(manifest.RegistryHash, registry.RegistryHash, StringComparison.Ordinal))
            found.Add("registry:hash_drift");
        foreach (var row in reference.Denominator ?? Array.Empty<AspxSurfaceDenominatorRow>())
        {
            foreach (var invalid in row.Validate()) found.Add(row.SurfaceId + ":" + invalid);
            if (row.ExpectedCountState == AspxExpectedCountState.Unknown) found.Add(row.SurfaceId + ":expected_count_unknown");
        }
        foreach (var item in reference.References ?? Array.Empty<AspxReferenceObservation>())
            foreach (var invalid in item.Validate()) found.Add(item.ReferenceObservationId + ":" + invalid);

        var hasUnknown = found.Count > 0 || physical.CoverageVerdict == DiscoveryVerdict.Unknown ||
            reference.CoverageVerdict == AspxAggregateVerdict.Unknown ||
            reference.Denominator.Any(row => row.TerminalOutcome is DiscoveryTerminalOutcome.Pending or
                DiscoveryTerminalOutcome.Unknown) ||
            reference.References.Any(item => item.Disposition == AspxReferenceDispositions.Unknown);
        var hasIncomplete = physical.CoverageVerdict == DiscoveryVerdict.Incomplete ||
            reference.CoverageVerdict == AspxAggregateVerdict.Incomplete ||
            reference.Denominator.Any(row => row.TerminalOutcome is DiscoveryTerminalOutcome.Denied or
                DiscoveryTerminalOutcome.Failed or DiscoveryTerminalOutcome.Truncated or
                DiscoveryTerminalOutcome.Cancelled) ||
            reference.References.Any(item => item.Disposition == AspxReferenceDispositions.ReferenceUnavailable);
        gaps = found.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (hasUnknown) return AspxAggregateVerdict.Unknown;
        if (hasIncomplete) return AspxAggregateVerdict.Incomplete;
        return AspxAggregateVerdict.CompleteAuthorizedSurface;
    }

    internal static async Task<(string Hash, long Length)> HashFileAsync(string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return (Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
    }
}

internal sealed record AspxAcquisitionEnvelopeValidation(
    AspxAggregateVerdict Verdict,
    bool PhysicalOnly,
    IReadOnlyList<string> GapCodes);

internal static class AspxAcquisitionEnvelopeValidator
{
    internal static AspxAcquisitionEnvelopeValidation PhysicalOnly(string physicalOutputVersion)
    {
        var gaps = physicalOutputVersion == AspxDiscoveryOutputV2.Version
            ? new[] { "reference_completeness_unsupported" }
            : new[] { "physical_output_version_unknown" };
        return new(AspxAggregateVerdict.Unknown, PhysicalOnly: true, gaps);
    }

    internal static AspxAcquisitionEnvelopeValidation Validate(AspxAcquisitionVerdictV1 envelope,
        string actualPhysicalHash, string actualReferenceHash, string physicalOutputVersion,
        string referenceOutputVersion)
    {
        if (envelope == null)
            return new(AspxAggregateVerdict.Unknown, false, new[] { "acquisition_envelope_missing" });
        var gaps = new HashSet<string>(StringComparer.Ordinal);
        if (envelope.OutputVersion != AspxAcquisitionVerdictV1.Version) gaps.Add("aggregate_version_unknown");
        if (physicalOutputVersion != AspxDiscoveryOutputV2.Version ||
            envelope.PhysicalVolume?.OutputVersion != AspxDiscoveryOutputV2.Version)
            gaps.Add("physical_version_incompatible");
        if (referenceOutputVersion != AspxReferenceOutputV1.Version ||
            envelope.ReferenceVolume?.OutputVersion != AspxReferenceOutputV1.Version)
            gaps.Add("reference_version_incompatible");
        if (envelope.PhysicalVolume == null || envelope.ReferenceVolume == null)
            gaps.Add("companion_volume_missing");
        else
        {
            if (envelope.PhysicalVolume.RunId != envelope.ReferenceVolume.RunId ||
                envelope.PhysicalVolume.RunId != envelope.AcquisitionRunId) gaps.Add("run_binding_mismatch");
            if (!string.Equals(envelope.PhysicalVolume.ProductRef, envelope.ReferenceVolume.ProductRef,
                StringComparison.Ordinal)) gaps.Add("product_ref_mismatch");
            if (!string.Equals(envelope.PhysicalVolume.ScopeAuthorityHash,
                envelope.ReferenceVolume.ScopeAuthorityHash, StringComparison.Ordinal)) gaps.Add("scope_authority_mismatch");
            if (!string.Equals(envelope.PhysicalVolume.SnapshotFence,
                envelope.ReferenceVolume.SnapshotFence, StringComparison.Ordinal)) gaps.Add("snapshot_fence_mismatch");
            if (!string.Equals(envelope.PhysicalVolume.Sha256, actualPhysicalHash, StringComparison.Ordinal))
                gaps.Add("physical_hash_mismatch");
            if (!string.Equals(envelope.ReferenceVolume.Sha256, actualReferenceHash, StringComparison.Ordinal))
                gaps.Add("reference_hash_mismatch");
        }
        return new(gaps.Count == 0 ? envelope.AggregateVerdict : AspxAggregateVerdict.Unknown,
            false, gaps.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }
}
