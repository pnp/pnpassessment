using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal sealed record AspxReferenceCandidate(
    string SourceKind,
    string SourceObjectId,
    string AcquisitionMethod,
    string ReferenceId,
    string RawLocator,
    string CanonicalRequestPath,
    string MatchedAlias,
    string Disposition,
    string ReasonCode,
    string LinkedFileUniqueId,
    string ContentOrigin,
    string PermissionContext,
    IReadOnlyList<string> EvidenceRefs);

internal sealed class AspxReferenceCollector
{
    private readonly object gate = new();
    private readonly List<AspxReferenceCandidate> candidates = new();
    private readonly List<AspxSurfaceDenominatorRow> denominator = new();
    private readonly List<AspxPaginationPageReceipt> pagination = new();
    private readonly HashSet<string> gaps = new(StringComparer.Ordinal);

    internal void AddReference(AspxReferenceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (gate) candidates.Add(candidate);
    }

    internal void AddSurface(AspxSurfaceDenominatorRow row,
        IReadOnlyList<AspxPaginationPageReceipt> pageReceipts,
        IReadOnlyList<string> gapCodes = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (gate)
        {
            denominator.Add(row);
            pagination.AddRange(pageReceipts ?? Array.Empty<AspxPaginationPageReceipt>());
            foreach (var gap in gapCodes ?? Array.Empty<string>()) gaps.Add(gap);
        }
    }

    internal void AddGap(string gap)
    {
        if (string.IsNullOrWhiteSpace(gap)) return;
        lock (gate) gaps.Add(gap);
    }

    internal AspxReferenceOutputV2 Build(Guid runId, AspxReferenceRunManifest manifest,
        AspxDiscoveryOutputV2 physical, AspxPlatformRegistryV1 registry)
    {
        AspxReferenceCandidate[] candidateSnapshot;
        AspxSurfaceDenominatorRow[] denominatorSnapshot;
        AspxPaginationPageReceipt[] paginationSnapshot;
        HashSet<string> gapSnapshot;
        lock (gate)
        {
            candidateSnapshot = candidates.ToArray();
            denominatorSnapshot = denominator.Select(row => row with
            {
                AcquisitionRunId = runId,
                SnapshotFence = manifest.SnapshotFence,
                ScopeAuthorityHash = manifest.ScopeAuthorityHash,
                ProviderVersion = manifest.ProviderVersion,
                ProductRef = manifest.ProductRef,
                SdkRef = manifest.SdkRef,
                PlatformBuildRef = manifest.PlatformBuildRef,
                RegistryRevision = manifest.RegistryRevision,
                RegistryHash = manifest.RegistryHash,
                ArtifactRunId = manifest.ArtifactRunId,
            }).ToArray();
            paginationSnapshot = pagination.ToArray();
            gapSnapshot = new HashSet<string>(gaps, StringComparer.Ordinal);
        }

        foreach (var invalid in manifest.Validate()) gapSnapshot.Add("reference_manifest:" + invalid);
        foreach (var invalid in registry.Validate(manifest.PlatformBuildRef)) gapSnapshot.Add("registry:" + invalid);
        if (!string.Equals(manifest.RegistryRevision, registry.RegistryRevision, StringComparison.Ordinal))
            gapSnapshot.Add("registry:revision_drift");
        if (!string.Equals(manifest.RegistryHash, registry.RegistryHash, StringComparison.Ordinal))
            gapSnapshot.Add("registry:hash_drift");

        var registryCompatible = registry.Validate(manifest.PlatformBuildRef).Count == 0 &&
            string.Equals(manifest.RegistryRevision, registry.RegistryRevision, StringComparison.Ordinal) &&
            string.Equals(manifest.RegistryHash, registry.RegistryHash, StringComparison.Ordinal);
        var registryEntries = registry.Entries ?? Array.Empty<AspxPlatformRegistryEntry>();
        denominatorSnapshot = denominatorSnapshot.Append(new AspxSurfaceDenominatorRow(
            AspxAcquisitionVersions.SurfaceContract, runId, manifest.SnapshotFence, manifest.ScopeAuthorityHash,
            "platform-registry", null, "platform-registry:" + registry.RegistryRevision,
            AspxSurfaceApplicability.SystemOrVirtualOnly, null, null, null, registry.ReviewRef, null,
            manifest.PlatformBuildRef, AspxRuntimeCounterexampleState.NoneObserved,
            registry.AuthorityKind, registry.AuthoritySourceRef, registry.RegistryRevision,
            registry.AuthorityArtifactHash, "READ", registry.AuthoritySourceRef, "registry entries", string.Empty,
            "independent-platform-registry", "reviewed-registry", registryCompatible ? registryEntries.Count : null,
            registryCompatible ? AspxExpectedCountState.Known : AspxExpectedCountState.Unknown,
            registryEntries.Count, registryCompatible
                ? registryEntries.Count == 0 ? DiscoveryTerminalOutcome.Empty : DiscoveryTerminalOutcome.Complete
                : DiscoveryTerminalOutcome.Unknown,
            registryCompatible ? "satisfied" : "unknown", false, DiscoveryHash.Of(registry.RegistryHash), 0,
            registryCompatible && registryEntries.Count == 0 ? "CompatibleRegistryNoEntries" : null,
            registryCompatible && registryEntries.Count == 0 ? registry.AuthoritySourceRef : null,
            manifest.ProviderVersion, manifest.ProductRef, manifest.SdkRef, manifest.PlatformBuildRef,
            manifest.RegistryRevision, manifest.RegistryHash, manifest.ArtifactRunId, DateTimeOffset.UtcNow,
            new[] { registry.AuthoritySourceRef, registry.ReviewRef }, "IndependentPlatformRegistry",
            "reference-only")).ToArray();

        var observations = candidateSnapshot.Select(candidate => Finalize(candidate, manifest, physical, gapSnapshot))
            .Concat((registry.Entries ?? Array.Empty<AspxPlatformRegistryEntry>()).Select(entry =>
                new AspxReferenceObservation(
                    DiscoveryHash.Of(runId.ToString("D"), AspxReferenceSourceKinds.PlatformRegistry, entry.ReferenceId),
                    AspxReferenceObservation.RequiredRecordKind, AspxReferenceSourceKinds.PlatformRegistry,
                    entry.ReferenceId, "IndependentPlatformRegistry", entry.ReferenceId, null,
                    AspxPlatformRegistryV1.NormalizeRequestPath(entry.CanonicalRequestPath), null,
                    manifest.PlatformBuildRef, registry.RegistryRevision, registry.RegistryHash,
                    string.Equals(entry.HandlerOrArtifactType, "VirtualHandler", StringComparison.OrdinalIgnoreCase)
                        ? AspxReferenceDispositions.VirtualHandler : AspxReferenceDispositions.ReferenceOnlyAvailable,
                    null, null, null,
                    string.Equals(entry.HandlerOrArtifactType, "VirtualHandler", StringComparison.OrdinalIgnoreCase)
                        ? "verified-registry-virtual" : "registry-reference-only",
                    "independent-platform-registry", new[] { registry.AuthoritySourceRef, registry.ReviewRef })))
            .OrderBy(item => item.ReferenceObservationId, StringComparer.Ordinal).ToArray();

        foreach (var observation in observations)
            foreach (var invalid in observation.Validate()) gapSnapshot.Add(observation.ReferenceObservationId + ":" + invalid);
        foreach (var row in denominatorSnapshot)
        {
            foreach (var invalid in row.Validate()) gapSnapshot.Add(row.SurfaceId + ":" + invalid);
            if (row.ExpectedCountState == AspxExpectedCountState.Unknown)
                gapSnapshot.Add(row.SurfaceId + ":expected_count_unknown");
        }

        var hasUnknown = gapSnapshot.Count > 0 || denominatorSnapshot.Any(row =>
            row.TerminalOutcome is DiscoveryTerminalOutcome.Pending or DiscoveryTerminalOutcome.Unknown) ||
            observations.Any(item => item.Disposition == AspxReferenceDispositions.Unknown);
        var hasIncomplete = denominatorSnapshot.Any(row =>
            row.TerminalOutcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or
                DiscoveryTerminalOutcome.Truncated or DiscoveryTerminalOutcome.Cancelled) ||
            observations.Any(item => item.Disposition == AspxReferenceDispositions.ReferenceUnavailable);
        var verdict = hasUnknown ? AspxAggregateVerdict.Unknown : hasIncomplete
            ? AspxAggregateVerdict.Incomplete : AspxAggregateVerdict.CompleteAuthorizedSurface;
        return new(AspxReferenceOutputV2.Version, runId, manifest.Hash(), verdict, observations,
            denominatorSnapshot.OrderBy(row => row.SurfaceId, StringComparer.Ordinal).ToArray(),
            paginationSnapshot.OrderBy(row => row.CollectionScopeKey, StringComparer.Ordinal)
                .ThenBy(row => row.PageOrdinal).ToArray(),
            gapSnapshot.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static AspxReferenceObservation Finalize(AspxReferenceCandidate candidate,
        AspxReferenceRunManifest manifest, AspxDiscoveryOutputV2 physical, ISet<string> gaps)
    {
        string canonicalKey = null;
        var disposition = candidate.Disposition;
        var fileUniqueId = candidate.LinkedFileUniqueId;
        if (!string.IsNullOrWhiteSpace(fileUniqueId))
        {
            var expectedKey = DiscoveryHash.Of("file", fileUniqueId);
            var matches = physical.Inventory.Where(row =>
                string.Equals(row.CanonicalInventoryKey, expectedKey, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 1)
            {
                canonicalKey = expectedKey;
            }
            else
            {
                gaps.Add("reference_physical_join_non_unique:" + candidate.SourceObjectId);
                disposition = AspxReferenceDispositions.Unknown;
                canonicalKey = null;
            }
        }

        var physicalDisposition = disposition is AspxReferenceDispositions.LinkedPhysicalGhosted or
            AspxReferenceDispositions.LinkedPhysicalCustomized;
        if (!physicalDisposition)
        {
            canonicalKey = null;
            if (disposition != AspxReferenceDispositions.Unknown) fileUniqueId = null;
        }

        return new(
            DiscoveryHash.Of(manifest.ArtifactRunId, candidate.SourceKind, candidate.SourceObjectId,
                candidate.RawLocator ?? string.Empty),
            AspxReferenceObservation.RequiredRecordKind, candidate.SourceKind, candidate.SourceObjectId,
            candidate.AcquisitionMethod, candidate.ReferenceId, candidate.RawLocator,
            candidate.CanonicalRequestPath, candidate.MatchedAlias, manifest.PlatformBuildRef,
            manifest.RegistryRevision, manifest.RegistryHash, disposition, candidate.ReasonCode,
            canonicalKey, fileUniqueId, candidate.ContentOrigin, candidate.PermissionContext,
            candidate.EvidenceRefs ?? Array.Empty<string>());
    }
}

internal sealed class AspxReferenceStore : IDisposable
{
    private readonly SqliteConnection connection;

    internal AspxReferenceStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False");
        connection.Open();
        Initialize();
    }

    internal void Write(AspxReferenceRunManifest manifest, AspxReferenceOutputV2 output, bool resume)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(output);
        var invalid = manifest.Validate();
        if (invalid.Count > 0) throw new InvalidOperationException(
            "Invalid immutable reference manifest: " + string.Join(", ", invalid));
        var runId = output.AcquisitionRunId.ToString("D");
        var existing = Scalar<string>("SELECT ManifestHash FROM ReferenceRuns WHERE RunId=$runId", ("$runId", runId));
        if (existing != null)
        {
            if (!resume) throw new InvalidOperationException("Reference run already exists; explicit resume is required.");
            ValidateResumeCompatibility(output.AcquisitionRunId, manifest);
            Execute("DELETE FROM ReferenceObservations WHERE RunId=$runId", ("$runId", runId));
            Execute("DELETE FROM ReferenceDenominator WHERE RunId=$runId", ("$runId", runId));
            Execute("DELETE FROM ReferencePaginationReceipts WHERE RunId=$runId", ("$runId", runId));
            Execute("DELETE FROM ReferenceGaps WHERE RunId=$runId", ("$runId", runId));
        }
        else
        {
            Execute("""
                INSERT INTO ReferenceRuns (RunId, ManifestJson, ManifestHash, OutputVersion, CoverageVerdict, UpdatedUtc)
                VALUES ($runId, $manifest, $hash, $version, $verdict, $updated)
                """, ("$runId", runId), ("$manifest", manifest.CanonicalJson()), ("$hash", manifest.Hash()),
                ("$version", output.OutputVersion), ("$verdict", output.CoverageVerdict.ToString()),
                ("$updated", DateTimeOffset.UtcNow.ToString("O")));
        }

        foreach (var observation in output.References)
            Execute("INSERT INTO ReferenceObservations (RunId, ObservationId, Json) VALUES ($runId, $id, $json)",
                ("$runId", runId), ("$id", observation.ReferenceObservationId),
                ("$json", JsonSerializer.Serialize(observation, AspxInventoryRuntime.JsonOptions())));
        foreach (var row in output.Denominator)
            Execute("INSERT INTO ReferenceDenominator (RunId, SurfaceId, Json) VALUES ($runId, $id, $json)",
                ("$runId", runId), ("$id", row.SurfaceId),
                ("$json", JsonSerializer.Serialize(row, AspxInventoryRuntime.JsonOptions())));
        foreach (var receipt in output.PaginationReceipts)
            Execute("""
                INSERT INTO ReferencePaginationReceipts (RunId, ScopeKey, PageOrdinal, Json)
                VALUES ($runId, $scope, $ordinal, $json)
                """, ("$runId", runId), ("$scope", receipt.CollectionScopeKey),
                ("$ordinal", receipt.PageOrdinal),
                ("$json", JsonSerializer.Serialize(receipt, AspxInventoryRuntime.JsonOptions())));
        foreach (var gap in output.GapCodes)
            Execute("INSERT INTO ReferenceGaps (RunId, GapCode) VALUES ($runId, $gap)",
                ("$runId", runId), ("$gap", gap));
        Execute("""
            UPDATE ReferenceRuns SET CoverageVerdict=$verdict, UpdatedUtc=$updated WHERE RunId=$runId
            """, ("$verdict", output.CoverageVerdict.ToString()),
            ("$updated", DateTimeOffset.UtcNow.ToString("O")), ("$runId", runId));
    }

    internal void ValidateResumeCompatibility(Guid runId, AspxReferenceRunManifest candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var id = runId.ToString("D");
        var storedHash = Scalar<string>("SELECT ManifestHash FROM ReferenceRuns WHERE RunId=$runId", ("$runId", id));
        if (storedHash == null)
            throw new InvalidOperationException($"Reference resume rejected: run '{id}' does not exist in the reference ledger. Start a new run or supply the matching v2 ledger.");
        var storedOutputVersion = Scalar<string>("SELECT OutputVersion FROM ReferenceRuns WHERE RunId=$runId", ("$runId", id));
        if (!string.Equals(storedOutputVersion, AspxReferenceOutputV2.Version, StringComparison.Ordinal))
            throw new InvalidOperationException($"Reference resume rejected: stored output version '{storedOutputVersion ?? "missing"}' is incompatible with '{AspxReferenceOutputV2.Version}'. Start a new run with new output paths; v1 receipts cannot be resumed as v2.");
        var storedJson = Scalar<string>("SELECT ManifestJson FROM ReferenceRuns WHERE RunId=$runId", ("$runId", id));
        AspxReferenceRunManifest stored;
        try
        {
            stored = JsonSerializer.Deserialize<AspxReferenceRunManifest>(storedJson,
                AspxInventoryRuntime.JsonOptions());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Reference resume rejected: stored immutable manifest JSON is invalid. Preserve the ledger and start a new v2 run.", ex);
        }
        if (stored == null || stored.ContractVersion != AspxAcquisitionVersions.ReferenceProducer ||
            stored.SchemaVersion != AspxAcquisitionVersions.ReferenceStore ||
            stored.ProviderVersion != AspxAcquisitionVersions.LiveProvider)
            throw new InvalidOperationException($"Reference resume rejected: stored contract/schema/provider versions are incompatible with {AspxAcquisitionVersions.ReferenceProducer} + {AspxAcquisitionVersions.ReferenceStore} + {AspxAcquisitionVersions.LiveProvider}. Preserve the ledger and start a new v2 run.");
        if (!string.Equals(storedHash, candidate.Hash(), StringComparison.Ordinal))
            throw new InvalidOperationException("Reference resume rejected: immutable v2 provenance drifted (product/sdk/authority/permission/registry/build/snapshot/provider binding). Preserve the prior run and use new output paths.");
    }

    public void Dispose() => connection.Dispose();

    private void Initialize() => Execute("""
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS ReferenceRuns (
          RunId TEXT PRIMARY KEY, ManifestJson TEXT NOT NULL, ManifestHash TEXT NOT NULL,
          OutputVersion TEXT NOT NULL, CoverageVerdict TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS ReferenceObservations (
          RunId TEXT NOT NULL, ObservationId TEXT NOT NULL, Json TEXT NOT NULL,
          PRIMARY KEY (RunId, ObservationId));
        CREATE TABLE IF NOT EXISTS ReferenceDenominator (
          RunId TEXT NOT NULL, SurfaceId TEXT NOT NULL, Json TEXT NOT NULL,
          PRIMARY KEY (RunId, SurfaceId));
        CREATE TABLE IF NOT EXISTS ReferencePaginationReceipts (
          RunId TEXT NOT NULL, ScopeKey TEXT NOT NULL, PageOrdinal INTEGER NOT NULL, Json TEXT NOT NULL,
          PRIMARY KEY (RunId, ScopeKey, PageOrdinal));
        CREATE TABLE IF NOT EXISTS ReferenceGaps (
          RunId TEXT NOT NULL, GapCode TEXT NOT NULL, PRIMARY KEY (RunId, GapCode));
        """);

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, parameterValue) in parameters)
            command.Parameters.AddWithValue(name, parameterValue ?? DBNull.Value);
        var scalarValue = command.ExecuteScalar();
        if (scalarValue == null || scalarValue == DBNull.Value) return default;
        return (T)Convert.ChangeType(scalarValue, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
