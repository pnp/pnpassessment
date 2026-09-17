using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal enum BatchCommitResult { Committed, ReplayNoOp, ReplayConflict }

internal sealed record DiscoveryInventoryRow(
    string ScopeKey, string CanonicalInventoryKey, string PhysicalLocator,
    string FileName, string IdentityQuality, string PermissionContext,
    PageDiscoveryState DiscoveryState = PageDiscoveryState.Discovered,
    string StableSourceObjectKey = null,
    Guid? SiteCollectionId = null,
    Guid? WebId = null,
    Guid? ListId = null,
    Guid? FolderUniqueId = null,
    int? ListItemId = null,
    Guid? FileUniqueId = null,
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
    string WebUrl = null,
    string MetadataJson = null);

internal sealed record DiscoveryObservationRow(
    string ScopeKey, string SourceKind, string ObservationKey, string FactHash,
    string FileName, string PhysicalLocator, string PermissionContext);

internal sealed record DiscoveryCoverageRow(
    string ScopeKey, string ParentScopeKey, string Kind, string SourceKind,
    DiscoveryTerminalOutcome Outcome, int? ExpectedCount, DiscoveryCounts Counts);

internal sealed record DiscoveryDenominatorRow(
    string ParentScopeKey, string ChildKind, DiscoveryTerminalOutcome Outcome,
    int ExpectedCount, int ObservedCount, string EnumerationFingerprint, string PermissionContext);

internal sealed record DiscoveryScopeDetailRow(
    string ScopeKey, string ParentScopeKey, string Kind, string SourceKind, string Locator,
    string PermissionContext, bool Required, DiscoveryTerminalOutcome Outcome,
    string ExclusionRuleId, string ExclusionRuleVersion, string ExclusionRuleHash,
    string ExclusionApprovalRef);

internal sealed record DiscoveryGapDetailRow(
    string ScopeKey, string GapKey, string SourceKind, string Code, string Detail, bool Resolved);

internal sealed record AspxAdmissionResult(bool IsAspx, string LeafName, string GapCode = null, string Detail = null);

internal static class AspxAdmission
{
    internal static AspxAdmissionResult Evaluate(RawDiscoveryRecord record)
    {
        var leaf = record.FileName;
        if (string.IsNullOrWhiteSpace(leaf) && record.LocatorIsGuaranteedPhysicalFilePath &&
            !string.IsNullOrWhiteSpace(record.PhysicalLocator))
        {
            leaf = record.PhysicalLocator.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        }
        if (string.IsNullOrWhiteSpace(leaf))
        {
            return new(false, null, DiscoveryGapCodes.FilenameMissing,
                "No file leaf name or guaranteed physical-file locator fallback was supplied.");
        }
        return new(string.Equals(Path.GetExtension(leaf), ".aspx", StringComparison.OrdinalIgnoreCase), leaf);
    }
}

internal sealed class DiscoveryStore : IDisposable
{
    private readonly SqliteConnection connection;

    internal DiscoveryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False");
        connection.Open();
        InitializeSchema();
    }

    internal void CreateRun(Guid runId, DiscoveryRunManifest manifest, string scopeMode, bool fixtureRun)
    {
        var invalid = manifest.Validate(fixtureRun);
        if (invalid.Count > 0) throw new InvalidOperationException("Invalid immutable discovery manifest: " + string.Join(", ", invalid));
        Execute("""
            INSERT INTO DiscoveryRuns
              (RunId, ManifestJson, ManifestHash, ScopeMode, ExecutionStatus, FixtureRun, CreatedUtc)
            VALUES ($runId, $manifest, $manifestHash, $scopeMode, $execution, $fixtureRun, $createdUtc)
            """,
            ("$runId", runId.ToString("D")), ("$manifest", manifest.CanonicalJson()),
            ("$manifestHash", DiscoveryHash.Of(manifest.CanonicalJson())), ("$scopeMode", scopeMode),
            ("$execution", DiscoveryExecutionStatus.Running.ToString()), ("$fixtureRun", fixtureRun ? 1 : 0),
            ("$createdUtc", DateTimeOffset.UtcNow.ToString("O")));
    }

    internal bool RunExists(Guid runId) => Scalar<long>(
        "SELECT COUNT(*) FROM DiscoveryRuns WHERE RunId=$runId", ("$runId", runId.ToString("D"))) == 1;

    internal DiscoveryRunManifest ReadManifest(Guid runId) => JsonSerializer.Deserialize<DiscoveryRunManifest>(
        Scalar<string>("SELECT ManifestJson FROM DiscoveryRuns WHERE RunId=$runId", ("$runId", runId.ToString("D"))),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    internal string ReadScopeMode(Guid runId) => Scalar<string>(
        "SELECT ScopeMode FROM DiscoveryRuns WHERE RunId=$runId", ("$runId", runId.ToString("D")));

    internal bool ReadFixtureRun(Guid runId) => Scalar<long>(
        "SELECT FixtureRun FROM DiscoveryRuns WHERE RunId=$runId", ("$runId", runId.ToString("D"))) == 1;

    internal void ResumeExecution(Guid runId) => Execute(
        "UPDATE DiscoveryRuns SET ExecutionStatus=$status, FinishedUtc=NULL WHERE RunId=$runId",
        ("$status", DiscoveryExecutionStatus.Running.ToString()), ("$runId", runId.ToString("D")));

    internal void RegisterScope(Guid runId, DiscoveryScopeRegistration scope)
    {
        if (!scope.Required && (string.IsNullOrWhiteSpace(scope.ExclusionRuleId) ||
            string.IsNullOrWhiteSpace(scope.ExclusionRuleVersion) || scope.ExclusionRuleHash?.Length != 64 ||
            string.IsNullOrWhiteSpace(scope.ExclusionApprovalRef)))
        {
            throw new InvalidOperationException("Policy exclusions require rule id/version/hash and approval ref.");
        }
        Execute("""
            INSERT INTO DiscoveryScopes
              (RunId, ScopeKey, ParentScopeKey, Kind, SourceKind, Locator, PermissionContext, Required, Outcome,
               ExclusionRuleId, ExclusionRuleVersion, ExclusionRuleHash, ExclusionApprovalRef)
            VALUES
              ($runId, $scopeKey, $parent, $kind, $source, $locator, $permission, $required, $outcome,
               $ruleId, $ruleVersion, $ruleHash, $approval)
            ON CONFLICT(RunId, ScopeKey) DO UPDATE SET
              ParentScopeKey=excluded.ParentScopeKey, Locator=excluded.Locator, PermissionContext=excluded.PermissionContext
            """,
            ("$runId", runId.ToString("D")), ("$scopeKey", scope.ScopeKey), ("$parent", scope.ParentScopeKey),
            ("$kind", scope.Kind.ToString()), ("$source", scope.SourceKind?.ToString()), ("$locator", scope.Locator),
            ("$permission", scope.PermissionContext), ("$required", scope.Required ? 1 : 0),
            ("$outcome", scope.Required ? DiscoveryTerminalOutcome.Pending.ToString() : DiscoveryTerminalOutcome.PolicyExcluded.ToString()),
            ("$ruleId", scope.ExclusionRuleId), ("$ruleVersion", scope.ExclusionRuleVersion),
            ("$ruleHash", scope.ExclusionRuleHash), ("$approval", scope.ExclusionApprovalRef));
    }

    internal void RecordChildEnumeration(Guid runId, string parentScopeKey, DiscoveryScopeKind childKind,
        IReadOnlyList<DiscoveryChildExpectation> expectedChildren, DiscoveryTerminalOutcome outcome,
        string permissionContext, string gapCode = null, string gapDetail = null)
    {
        expectedChildren ??= Array.Empty<DiscoveryChildExpectation>();
        if (expectedChildren.Any(child => child.Kind != childKind))
            throw new InvalidOperationException("Child denominator entries must use the declared child kind.");

        var canonical = string.Join("\n", expectedChildren.OrderBy(child => child.ScopeKey, StringComparer.Ordinal)
            .Select(child => string.Join("|", child.ScopeKey, child.Kind, child.SourceKind, child.Locator,
                child.PermissionContext, child.Required)));
        var fingerprint = DiscoveryHash.Of(parentScopeKey, childKind.ToString(), canonical);
        using var transaction = connection.BeginTransaction();
        var previous = Scalar<string>("""
            SELECT EnumerationFingerprint FROM DiscoveryChildEnumerations
            WHERE RunId=$runId AND ParentScopeKey=$parent AND ChildKind=$kind
            """, transaction, ("$runId", runId.ToString("D")), ("$parent", parentScopeKey),
            ("$kind", childKind.ToString()));
        if (!string.IsNullOrWhiteSpace(previous) && !string.Equals(previous, fingerprint, StringComparison.Ordinal))
        {
            UpsertGap(runId, parentScopeKey, DiscoverySourceKind.TenantManifest,
                DiscoveryGapCodes.DenominatorDrift,
                $"Expected {childKind} denominator changed from {previous} to {fingerprint} during resume/replay.",
                null, transaction);
        }

        Execute(transaction, """
            INSERT INTO DiscoveryChildEnumerations
              (RunId, ParentScopeKey, ChildKind, Outcome, ExpectedCount, EnumerationFingerprint, PermissionContext, UpdatedUtc)
            VALUES ($runId, $parent, $kind, $outcome, $count, $fingerprint, $permission, $updated)
            ON CONFLICT(RunId, ParentScopeKey, ChildKind) DO UPDATE SET
              Outcome=excluded.Outcome, ExpectedCount=excluded.ExpectedCount,
              EnumerationFingerprint=excluded.EnumerationFingerprint,
              PermissionContext=excluded.PermissionContext, UpdatedUtc=excluded.UpdatedUtc
            """, ("$runId", runId.ToString("D")), ("$parent", parentScopeKey),
            ("$kind", childKind.ToString()), ("$outcome", outcome.ToString()),
            ("$count", expectedChildren.Count(child => child.Required)), ("$fingerprint", fingerprint),
            ("$permission", permissionContext), ("$updated", DateTimeOffset.UtcNow.ToString("O")));

        foreach (var child in expectedChildren)
        {
            Execute(transaction, """
                INSERT INTO DiscoveryExpectedChildren
                  (RunId, ParentScopeKey, ChildScopeKey, ChildKind, SourceKind, Locator, PermissionContext, Required)
                VALUES ($runId, $parent, $child, $kind, $source, $locator, $permission, $required)
                ON CONFLICT(RunId, ParentScopeKey, ChildScopeKey) DO UPDATE SET
                  ChildKind=excluded.ChildKind, SourceKind=excluded.SourceKind, Locator=excluded.Locator,
                  PermissionContext=excluded.PermissionContext, Required=excluded.Required
                """, ("$runId", runId.ToString("D")), ("$parent", parentScopeKey),
                ("$child", child.ScopeKey), ("$kind", child.Kind.ToString()),
                ("$source", child.SourceKind?.ToString()), ("$locator", child.Locator),
                ("$permission", child.PermissionContext), ("$required", child.Required ? 1 : 0));
        }
        if (!string.IsNullOrWhiteSpace(gapCode))
            UpsertGap(runId, parentScopeKey, DiscoverySourceKind.TenantManifest, gapCode, gapDetail, null, transaction);
        transaction.Commit();
    }

    internal void RecordGap(Guid runId, string scopeKey, string code, string detail) =>
        UpsertGap(runId, scopeKey, DiscoverySourceKind.TenantManifest, code, detail, null, null);

    internal Guid BeginAttempt(Guid runId, string scopeKey, DiscoverySourceKind sourceKind)
    {
        Execute("""
            UPDATE DiscoveryAttempts SET Status=$interrupted, FinishedUtc=$finished
            WHERE RunId=$runId AND ScopeKey=$scopeKey AND SourceKind=$source AND Status=$running
            """,
            ("$interrupted", DiscoveryAttemptStatus.Interrupted.ToString()), ("$finished", DateTimeOffset.UtcNow.ToString("O")),
            ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey), ("$source", sourceKind.ToString()),
            ("$running", DiscoveryAttemptStatus.Running.ToString()));
        var attemptId = Guid.NewGuid();
        Execute("""
            INSERT INTO DiscoveryAttempts (AttemptId, RunId, ScopeKey, SourceKind, Status, StartedUtc)
            VALUES ($attempt, $runId, $scopeKey, $source, $status, $started)
            """,
            ("$attempt", attemptId.ToString("D")), ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey),
            ("$source", sourceKind.ToString()), ("$status", DiscoveryAttemptStatus.Running.ToString()),
            ("$started", DateTimeOffset.UtcNow.ToString("O")));
        return attemptId;
    }

    internal BatchCommitResult CommitBatch(Guid runId, string scopeKey, DiscoverySourceKind sourceKind, Guid attemptId, RawDiscoveryBatch batch)
    {
        using var transaction = connection.BeginTransaction();
        var existing = ExistingBatch(attemptId, batch.BatchOrdinal, transaction);
        if (existing != null)
        {
            if (existing.Value.Request == batch.RequestFingerprint && existing.Value.Response == batch.ResponseFingerprint)
            {
                transaction.Rollback();
                return BatchCommitResult.ReplayNoOp;
            }
            UpsertGap(runId, scopeKey, sourceKind, DiscoveryGapCodes.BatchReplayConflict,
                $"Batch ordinal {batch.BatchOrdinal} changed fingerprints.", null, transaction);
            SetAttempt(attemptId, DiscoveryAttemptStatus.Failed, transaction);
            SetOutcome(runId, scopeKey, DiscoveryTerminalOutcome.Unknown, transaction);
            transaction.Commit();
            return BatchCommitResult.ReplayConflict;
        }

        var batchId = Guid.NewGuid();
        Execute(transaction, """
            INSERT INTO DiscoveryBatches
              (BatchId, AttemptId, BatchOrdinal, RequestFingerprint, ResponseFingerprint, TerminalFlag, NextCheckpoint, CommittedUtc)
            VALUES ($batch, $attempt, $ordinal, $request, $response, $terminal, $checkpoint, $committed)
            """,
            ("$batch", batchId.ToString("D")), ("$attempt", attemptId.ToString("D")), ("$ordinal", batch.BatchOrdinal),
            ("$request", batch.RequestFingerprint), ("$response", batch.ResponseFingerprint),
            ("$terminal", batch.IsTerminal ? 1 : 0), ("$checkpoint", batch.NextCheckpoint),
            ("$committed", DateTimeOffset.UtcNow.ToString("O")));

        foreach (var record in batch.Records ?? Array.Empty<RawDiscoveryRecord>())
            PersistRecord(runId, scopeKey, sourceKind, attemptId, batchId, record, transaction);

        if (!string.IsNullOrWhiteSpace(batch.GapCode))
            UpsertGap(runId, scopeKey, sourceKind, batch.GapCode, batch.GapDetail, null, transaction);

        if (batch.IsTerminal)
        {
            SetAttempt(attemptId,
                batch.TerminalOutcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty
                    ? DiscoveryAttemptStatus.Complete : DiscoveryAttemptStatus.Failed, transaction);
            SetOutcome(runId, scopeKey, batch.TerminalOutcome, transaction);
        }
        transaction.Commit();
        return BatchCommitResult.Committed;
    }

    internal void FinishExecution(Guid runId, DiscoveryExecutionStatus status) => Execute(
        "UPDATE DiscoveryRuns SET ExecutionStatus=$status, FinishedUtc=$finished WHERE RunId=$runId",
        ("$status", status.ToString()), ("$finished", DateTimeOffset.UtcNow.ToString("O")), ("$runId", runId.ToString("D")));

    internal void RecordScopeOutcome(Guid runId, string scopeKey, DiscoveryTerminalOutcome outcome) =>
        Execute("UPDATE DiscoveryScopes SET Outcome=$outcome WHERE RunId=$runId AND ScopeKey=$scopeKey",
            ("$outcome", outcome.ToString()), ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey));

    internal DiscoveryExecutionStatus ReadExecutionStatus(Guid runId) => Enum.Parse<DiscoveryExecutionStatus>(
        Scalar<string>("SELECT ExecutionStatus FROM DiscoveryRuns WHERE RunId=$runId", ("$runId", runId.ToString("D"))));

    internal DiscoveryVerdict EvaluateVerdict(Guid runId, bool tenantVisibilityVerified)
    {
        var scopeMode = Scalar<string>("SELECT ScopeMode FROM DiscoveryRuns WHERE RunId=$runId", ("$runId", runId.ToString("D")));
        if (AspxScopeModes.RequiresTenantDenominator(scopeMode)) EnsureTenantFullDenominator(runId);
        var outcomes = Strings("SELECT Outcome FROM DiscoveryScopes WHERE RunId=$runId", ("$runId", runId.ToString("D")))
            .Select(Enum.Parse<DiscoveryTerminalOutcome>).ToArray();
        var enumerationOutcomes = Strings("SELECT Outcome FROM DiscoveryChildEnumerations WHERE RunId=$runId", ("$runId", runId.ToString("D")))
            .Select(Enum.Parse<DiscoveryTerminalOutcome>).ToArray();
        var gaps = Strings("SELECT Code FROM DiscoveryGaps WHERE RunId=$runId AND Resolved=0", ("$runId", runId.ToString("D"))).ToArray();
        var conflicts = Scalar<long>("SELECT COUNT(*) FROM DiscoveryConflicts WHERE RunId=$runId AND Resolved=0", ("$runId", runId.ToString("D")));
        DiscoveryVerdict verdict;
        if (outcomes.Length == 0 || outcomes.Any(outcome => outcome is DiscoveryTerminalOutcome.Pending or DiscoveryTerminalOutcome.Unknown) ||
            enumerationOutcomes.Any(outcome => outcome is DiscoveryTerminalOutcome.Pending or DiscoveryTerminalOutcome.Unknown) ||
            gaps.Any(DiscoveryGapCodes.ForcesUnknown) || conflicts > 0)
            verdict = DiscoveryVerdict.Unknown;
        else if (outcomes.Any(outcome => outcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or
                     DiscoveryTerminalOutcome.Truncated or DiscoveryTerminalOutcome.Cancelled) ||
                 enumerationOutcomes.Any(outcome => outcome is DiscoveryTerminalOutcome.Denied or DiscoveryTerminalOutcome.Failed or
                     DiscoveryTerminalOutcome.Truncated or DiscoveryTerminalOutcome.Cancelled) ||
                 gaps.Contains(DiscoveryGapCodes.ExpectedChildMissing, StringComparer.Ordinal))
            verdict = DiscoveryVerdict.Incomplete;
        else if (!AspxScopeModes.RequiresTenantDenominator(scopeMode) || outcomes.Contains(DiscoveryTerminalOutcome.PolicyExcluded))
            verdict = DiscoveryVerdict.CompleteDeclaredSubset;
        else
            verdict = tenantVisibilityVerified ? DiscoveryVerdict.CompleteTenantVerified : DiscoveryVerdict.CompleteAuthorizedSurface;
        Execute("UPDATE DiscoveryRuns SET Verdict=$verdict WHERE RunId=$runId", ("$verdict", verdict.ToString()), ("$runId", runId.ToString("D")));
        return verdict;
    }

    internal DiscoveryCounts GetCounts(Guid runId, string scopeKey)
    {
        var attempt = Scalar<string>("""
            SELECT AttemptId FROM DiscoveryAttempts WHERE RunId=$runId AND ScopeKey=$scopeKey
            ORDER BY CASE WHEN Status='Complete' THEN 0 ELSE 1 END, StartedUtc DESC LIMIT 1
            """, ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey));
        long AttemptCount(string table, string suffix = "") => string.IsNullOrEmpty(attempt) ? 0 : Scalar<long>(
            $"SELECT COUNT(*) FROM {table} WHERE AttemptId=$attempt {suffix}", ("$attempt", attempt));
        return new(
            (int)AttemptCount("DiscoveryAttemptObservations"),
            (int)AttemptCount("DiscoveryAttemptObservations", "AND Emitted=1"),
            (int)Scalar<long>("SELECT COUNT(*) FROM DiscoveryInventory WHERE RunId=$runId AND ScopeKey=$scopeKey", ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey)),
            (int)AttemptCount("DiscoveryBatches"),
            (int)Scalar<long>("SELECT COUNT(*) FROM DiscoveryAttempts WHERE RunId=$runId AND ScopeKey=$scopeKey", ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey)),
            (int)Scalar<long>("SELECT COUNT(*) FROM DiscoveryGaps WHERE RunId=$runId AND ScopeKey=$scopeKey AND Resolved=0", ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey)),
            (int)Scalar<long>("SELECT COUNT(*) FROM DiscoveryConflicts WHERE RunId=$runId AND ScopeKey=$scopeKey AND Resolved=0", ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey)));
    }

    internal IReadOnlyList<DiscoveryInventoryRow> ReadInventory(Guid runId)
    {
        using var command = Command("""
            SELECT ScopeKey, CanonicalInventoryKey, PhysicalLocator, FileName, IdentityQuality, PermissionContext,
                   DiscoveryState, StableSourceObjectKey, SiteCollectionId, WebId, ListId, FolderUniqueId,
                   ListItemId, FileUniqueId, HomePage, ContentTypeId, PageType, LibraryHidden,
                   CustomizedPageStatusRaw, ObservationMethod, SelectionState, WelcomePageStatus,
                   ScanId, SiteUrl, WebUrl, MetadataJson
            FROM DiscoveryInventory WHERE RunId=$runId ORDER BY CanonicalInventoryKey
            """, null, ("$runId", runId.ToString("D")));
        using var reader = command.ExecuteReader();
        var rows = new List<DiscoveryInventoryRow>();
        while (reader.Read()) rows.Add(new(
            reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            Enum.Parse<PageDiscoveryState>(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7),
            ReadGuid(reader, 8), ReadGuid(reader, 9), ReadGuid(reader, 10), ReadGuid(reader, 11),
            reader.IsDBNull(12) ? null : reader.GetInt32(12), ReadGuid(reader, 13), ReadNullableBool(reader, 14),
            reader.IsDBNull(15) ? null : reader.GetString(15), reader.IsDBNull(16) ? null : reader.GetString(16),
            ReadNullableBool(reader, 17), reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19), reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetString(21), reader.IsDBNull(22) ? null : reader.GetString(22),
            reader.IsDBNull(23) ? null : reader.GetString(23), reader.IsDBNull(24) ? null : reader.GetString(24),
            reader.IsDBNull(25) ? null : reader.GetString(25)));
        return rows;
    }

    internal IReadOnlyList<DiscoveryObservationRow> ReadObservations(Guid runId)
    {
        using var command = Command("""
            SELECT ScopeKey, SourceKind, ObservationKey, FactHash, FileName, PhysicalLocator, PermissionContext
            FROM DiscoveryObservations WHERE RunId=$runId ORDER BY ObservationKey, FactHash
            """, null, ("$runId", runId.ToString("D")));
        using var reader = command.ExecuteReader();
        var rows = new List<DiscoveryObservationRow>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6)));
        return rows;
    }

    internal IReadOnlyList<DiscoveryCoverageRow> ReadCoverage(Guid runId)
    {
        using var command = Command("""
            SELECT ScopeKey, ParentScopeKey, Kind, SourceKind, Outcome
            FROM DiscoveryScopes WHERE RunId=$runId ORDER BY ScopeKey
            """, null, ("$runId", runId.ToString("D")));
        using var reader = command.ExecuteReader();
        var raw = new List<(string Key, string Parent, string Kind, string Source, DiscoveryTerminalOutcome Outcome)>();
        while (reader.Read()) raw.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), Enum.Parse<DiscoveryTerminalOutcome>(reader.GetString(4))));
        reader.Close();
        return raw.Select(row => new DiscoveryCoverageRow(row.Key, row.Parent, row.Kind, row.Source, row.Outcome,
            ExpectedCount: Scalar<long>("SELECT COALESCE(SUM(ExpectedCount), -1) FROM DiscoveryChildEnumerations WHERE RunId=$runId AND ParentScopeKey=$parent",
                ("$runId", runId.ToString("D")), ("$parent", row.Key)) is var expected && expected >= 0 ? (int)expected : null,
            GetCounts(runId, row.Key))).ToArray();
    }

    internal IReadOnlyList<DiscoveryDenominatorRow> ReadDenominator(Guid runId)
    {
        using var command = Command("""
            SELECT e.ParentScopeKey, e.ChildKind, e.Outcome, e.ExpectedCount,
                   (SELECT COUNT(*) FROM DiscoveryExpectedChildren x
                    JOIN DiscoveryScopes s ON s.RunId=x.RunId AND s.ScopeKey=x.ChildScopeKey
                    WHERE x.RunId=e.RunId AND x.ParentScopeKey=e.ParentScopeKey
                      AND x.ChildKind=e.ChildKind AND x.Required=1),
                   e.EnumerationFingerprint, e.PermissionContext
            FROM DiscoveryChildEnumerations e
            WHERE e.RunId=$runId
            ORDER BY e.ParentScopeKey, e.ChildKind
            """, null, ("$runId", runId.ToString("D")));
        using var reader = command.ExecuteReader();
        var rows = new List<DiscoveryDenominatorRow>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1),
            Enum.Parse<DiscoveryTerminalOutcome>(reader.GetString(2)), reader.GetInt32(3), reader.GetInt32(4),
            reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return rows;
    }

    internal IReadOnlyList<string> ReadGapCodes(Guid runId) => Strings(
        "SELECT Code FROM DiscoveryGaps WHERE RunId=$runId AND Resolved=0 ORDER BY Code", ("$runId", runId.ToString("D"))).ToArray();

    internal IReadOnlyList<DiscoveryGapDetailRow> ReadGaps(Guid runId)
    {
        using var command = Command("""
            SELECT ScopeKey, GapKey, SourceKind, Code, Detail, Resolved
            FROM DiscoveryGaps WHERE RunId=$runId ORDER BY ScopeKey, Code, GapKey
            """, null, ("$runId", runId.ToString("D")));
        using var reader = command.ExecuteReader();
        var rows = new List<DiscoveryGapDetailRow>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5) != 0));
        return rows;
    }

    internal IReadOnlyList<DiscoveryScopeDetailRow> ReadScopes(Guid runId)
    {
        using var command = Command("""
            SELECT ScopeKey, ParentScopeKey, Kind, SourceKind, Locator, PermissionContext, Required, Outcome,
                   ExclusionRuleId, ExclusionRuleVersion, ExclusionRuleHash, ExclusionApprovalRef
            FROM DiscoveryScopes WHERE RunId=$runId ORDER BY ScopeKey
            """, null, ("$runId", runId.ToString("D")));
        using var reader = command.ExecuteReader();
        var rows = new List<DiscoveryScopeDetailRow>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6) != 0, Enum.Parse<DiscoveryTerminalOutcome>(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
        return rows;
    }
    internal int ReadConflictCount(Guid runId) => (int)Scalar<long>(
        "SELECT COUNT(*) FROM DiscoveryConflicts WHERE RunId=$runId AND Resolved=0", ("$runId", runId.ToString("D")));

    public void Dispose() => connection.Dispose();

    private void EnsureTenantFullDenominator(Guid runId)
    {
        Execute("UPDATE DiscoveryGaps SET Resolved=1 WHERE RunId=$runId AND Code=$code",
            ("$runId", runId.ToString("D")), ("$code", DiscoveryGapCodes.ExpectedChildMissing));
        var requiredKinds = new[]
        {
            DiscoveryScopeKind.Tenant, DiscoveryScopeKind.Geo, DiscoveryScopeKind.SiteCollection,
            DiscoveryScopeKind.Web, DiscoveryScopeKind.Container, DiscoveryScopeKind.Folder,
        };
        foreach (var kind in requiredKinds)
        {
            if (Scalar<long>("SELECT COUNT(*) FROM DiscoveryScopes WHERE RunId=$runId AND Required=1 AND Kind=$kind",
                    ("$runId", runId.ToString("D")), ("$kind", kind.ToString())) == 0)
            {
                RecordGap(runId, "run:" + runId.ToString("D"), DiscoveryGapCodes.ExpectedChildMissing,
                    $"tenant_full requires at least one required {kind} scope.");
            }
        }

        using (var missingCommand = Command("""
            SELECT x.ParentScopeKey, x.ChildScopeKey, x.ChildKind
            FROM DiscoveryExpectedChildren x
            LEFT JOIN DiscoveryScopes s ON s.RunId=x.RunId AND s.ScopeKey=x.ChildScopeKey
            WHERE x.RunId=$runId AND x.Required=1 AND s.ScopeKey IS NULL
            ORDER BY x.ParentScopeKey, x.ChildScopeKey
            """, null, ("$runId", runId.ToString("D"))))
        using (var reader = missingCommand.ExecuteReader())
        {
            var missing = new List<(string Parent, string Child, string Kind)>();
            while (reader.Read()) missing.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            reader.Close();
            foreach (var item in missing)
                RecordGap(runId, item.Parent, DiscoveryGapCodes.ExpectedChildMissing,
                    $"Expected required {item.Kind} child '{item.Child}' was not observed.");
        }

        var parentChildKinds = new Dictionary<DiscoveryScopeKind, DiscoveryScopeKind>
        {
            [DiscoveryScopeKind.Tenant] = DiscoveryScopeKind.Geo,
            [DiscoveryScopeKind.Geo] = DiscoveryScopeKind.SiteCollection,
            [DiscoveryScopeKind.SiteCollection] = DiscoveryScopeKind.Web,
            [DiscoveryScopeKind.Web] = DiscoveryScopeKind.Container,
            [DiscoveryScopeKind.Container] = DiscoveryScopeKind.Folder,
            [DiscoveryScopeKind.Folder] = DiscoveryScopeKind.Folder,
        };
        using var parentCommand = Command(
            "SELECT ScopeKey, Kind FROM DiscoveryScopes WHERE RunId=$runId AND Required=1 ORDER BY ScopeKey",
            null, ("$runId", runId.ToString("D")));
        using var parentReader = parentCommand.ExecuteReader();
        var parents = new List<(string Key, DiscoveryScopeKind Kind)>();
        while (parentReader.Read())
            parents.Add((parentReader.GetString(0), Enum.Parse<DiscoveryScopeKind>(parentReader.GetString(1))));
        parentReader.Close();
        foreach (var parent in parents)
        {
            var childKind = parentChildKinds[parent.Kind];
            var count = Scalar<long>("""
                SELECT COUNT(*) FROM DiscoveryChildEnumerations
                WHERE RunId=$runId AND ParentScopeKey=$parent AND ChildKind=$kind
                """, ("$runId", runId.ToString("D")), ("$parent", parent.Key), ("$kind", childKind.ToString()));
            if (count == 0)
            {
                RecordGap(runId, parent.Key, DiscoveryGapCodes.ExpectedChildMissing,
                    $"Required {parent.Kind} scope has no {childKind} child enumeration denominator.");
            }
        }

        using var singleContainerCommand = Command("""
            SELECT ParentScopeKey FROM DiscoveryChildEnumerations
            WHERE RunId=$runId AND ChildKind='Container' AND ExpectedCount=1
            ORDER BY ParentScopeKey
            """, null, ("$runId", runId.ToString("D")));
        using var singleContainerReader = singleContainerCommand.ExecuteReader();
        var singleContainerParents = new List<string>();
        while (singleContainerReader.Read()) singleContainerParents.Add(singleContainerReader.GetString(0));
        singleContainerReader.Close();
        foreach (var parent in singleContainerParents)
            RecordGap(runId, parent, DiscoveryGapCodes.ExpectedChildMissing,
                "tenant_full cannot prove a complete web denominator from a single Container expectation.");
    }

    private void PersistRecord(Guid runId, string scopeKey, DiscoverySourceKind sourceKind, Guid attemptId, Guid batchId,
        RawDiscoveryRecord record, SqliteTransaction transaction)
    {
        var locator = NormalizeLocator(record.PhysicalLocator);
        var sourceObjectKey = !string.IsNullOrWhiteSpace(record.FileUniqueId) ? "file:" + record.FileUniqueId :
            !string.IsNullOrWhiteSpace(record.NativeObjectId) ? "native:" + record.NativeObjectId :
            !string.IsNullOrWhiteSpace(record.ContainerStableId) && !string.IsNullOrWhiteSpace(locator) ? "locator:" + record.ContainerStableId + ":" + locator : null;
        if (sourceObjectKey == null)
        {
            UpsertGap(runId, scopeKey, sourceKind, DiscoveryGapCodes.IdentityMissing, "No stable identity was supplied.", null, transaction);
            return;
        }

        var observationKey = DiscoveryHash.Of(DiscoveryRunManifest.CurrentContractVersion, scopeKey, sourceKind.ToString(), sourceObjectKey);
        var metadata = DiscoveryHash.Metadata(record.Metadata);
        var factHash = DiscoveryHash.Of(record.FileUniqueId, record.ContainerStableId, record.FileName, locator,
            record.LocatorIsGuaranteedPhysicalFilePath.ToString(), record.PermissionContext, metadata,
            record.SiteCollectionId?.ToString("D"), record.WebId?.ToString("D"), record.ListId?.ToString("D"),
            record.FolderUniqueId?.ToString("D"), record.ListItemId?.ToString(), record.HomePage?.ToString(),
            record.ContentTypeId, record.PageType, record.LibraryHidden?.ToString(),
            record.CustomizedPageStatusRaw, record.ObservationMethod, record.SelectionState,
            record.WelcomePageStatus, record.ScanId, record.SiteUrl, record.WebUrl);
        var changed = Scalar<long>("SELECT COUNT(*) FROM DiscoveryObservations WHERE RunId=$runId AND ObservationKey=$key AND FactHash<>$fact",
            transaction, ("$runId", runId.ToString("D")), ("$key", observationKey), ("$fact", factHash)) > 0;
        Execute(transaction, """
            INSERT OR IGNORE INTO DiscoveryObservations
              (ObservationId, RunId, ScopeKey, SourceKind, ObservationKey, FactHash, SourceObjectKey, FileName, PhysicalLocator, PermissionContext, MetadataJson)
            VALUES ($id, $runId, $scopeKey, $source, $key, $fact, $sourceObject, $fileName, $locator, $permission, $metadata)
            """,
            ("$id", Guid.NewGuid().ToString("D")), ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey),
            ("$source", sourceKind.ToString()), ("$key", observationKey), ("$fact", factHash), ("$sourceObject", sourceObjectKey),
            ("$fileName", record.FileName), ("$locator", locator), ("$permission", record.PermissionContext), ("$metadata", metadata));
        var observationId = Scalar<string>("SELECT ObservationId FROM DiscoveryObservations WHERE RunId=$runId AND ObservationKey=$key AND FactHash=$fact",
            transaction, ("$runId", runId.ToString("D")), ("$key", observationKey), ("$fact", factHash));
        var admission = AspxAdmission.Evaluate(record);
        Execute(transaction, """
            INSERT OR IGNORE INTO DiscoveryAttemptObservations (AttemptId, ObservationId, BatchId, Emitted)
            VALUES ($attempt, $observation, $batch, $emitted)
            """, ("$attempt", attemptId.ToString("D")), ("$observation", observationId),
            ("$batch", batchId.ToString("D")), ("$emitted", admission.IsAspx ? 1 : 0));
        if (changed) UpsertGap(runId, scopeKey, sourceKind, DiscoveryGapCodes.ChangedDuringScan,
            "Stable observation identity changed facts during the scan.", observationKey, transaction);
        if (!admission.IsAspx)
        {
            if (admission.GapCode != null) UpsertGap(runId, scopeKey, sourceKind, admission.GapCode, admission.Detail, observationKey, transaction);
            return;
        }

        var canonicalKey = CanonicalInventoryKey(record, locator);
        if (!string.IsNullOrWhiteSpace(locator))
        {
            using var command = Command("SELECT CanonicalInventoryKey FROM DiscoveryInventory WHERE RunId=$runId AND PhysicalLocator=$locator AND CanonicalInventoryKey<>$key",
                transaction, ("$runId", runId.ToString("D")), ("$locator", locator), ("$key", canonicalKey));
            using var reader = command.ExecuteReader();
            var participants = new List<string>();
            while (reader.Read()) participants.Add(reader.GetString(0));
            reader.Close();
            foreach (var participant in participants)
            {
                var sorted = new[] { participant, canonicalKey }.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                var conflictKey = DiscoveryHash.Of(scopeKey, DiscoveryGapCodes.LocatorIdentityConflict, sorted[0], sorted[1]);
                Execute(transaction, """
                    INSERT OR IGNORE INTO DiscoveryConflicts (RunId, ScopeKey, ConflictKey, Code, ParticipantKeys, Resolved)
                    VALUES ($runId, $scopeKey, $key, $code, $participants, 0)
                    """, ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey), ("$key", conflictKey),
                    ("$code", DiscoveryGapCodes.LocatorIdentityConflict), ("$participants", string.Join(',', sorted)));
            }
        }
        Execute(transaction, """
            INSERT INTO DiscoveryInventory
              (RunId, ScopeKey, CanonicalInventoryKey, PhysicalLocator, FileName, IdentityQuality,
               PermissionContext, DiscoveryState, StableSourceObjectKey, SiteCollectionId, WebId, ListId,
               FolderUniqueId, ListItemId, FileUniqueId, HomePage, ContentTypeId, PageType, LibraryHidden,
               CustomizedPageStatusRaw, ObservationMethod, SelectionState, WelcomePageStatus,
               ScanId, SiteUrl, WebUrl, MetadataJson)
            VALUES
              ($runId, $scopeKey, $key, $locator, $fileName, $quality, $permission, $state, $sourceObject,
               $siteCollectionId, $webId, $listId, $folderUniqueId, $listItemId, $fileUniqueId,
               $homePage, $contentTypeId, $pageType, $libraryHidden, $customizedPageStatusRaw,
               $observationMethod, $selectionState, $welcomePageStatus, $scanId, $siteUrl, $webUrl, $metadata)
            ON CONFLICT(RunId, CanonicalInventoryKey) DO UPDATE SET
              ScopeKey=excluded.ScopeKey, PhysicalLocator=excluded.PhysicalLocator, FileName=excluded.FileName,
              PermissionContext=excluded.PermissionContext, DiscoveryState=excluded.DiscoveryState,
              StableSourceObjectKey=excluded.StableSourceObjectKey, SiteCollectionId=excluded.SiteCollectionId,
              WebId=excluded.WebId, ListId=excluded.ListId, FolderUniqueId=excluded.FolderUniqueId,
              ListItemId=excluded.ListItemId, FileUniqueId=excluded.FileUniqueId, HomePage=excluded.HomePage,
              ContentTypeId=excluded.ContentTypeId, PageType=excluded.PageType,
              LibraryHidden=excluded.LibraryHidden, CustomizedPageStatusRaw=excluded.CustomizedPageStatusRaw,
              ObservationMethod=excluded.ObservationMethod, SelectionState=excluded.SelectionState,
              WelcomePageStatus=excluded.WelcomePageStatus, ScanId=excluded.ScanId,
              SiteUrl=excluded.SiteUrl, WebUrl=excluded.WebUrl, MetadataJson=excluded.MetadataJson
            """, ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey), ("$key", canonicalKey), ("$locator", locator),
            ("$fileName", admission.LeafName), ("$quality", string.IsNullOrWhiteSpace(record.FileUniqueId) ? "fallback" : "strong"),
            ("$permission", record.PermissionContext), ("$state", PageDiscoveryState.Discovered.ToString()),
            ("$sourceObject", sourceObjectKey), ("$siteCollectionId", record.SiteCollectionId?.ToString("D")),
            ("$webId", record.WebId?.ToString("D")), ("$listId", record.ListId?.ToString("D")),
            ("$folderUniqueId", record.FolderUniqueId?.ToString("D")), ("$listItemId", record.ListItemId),
            ("$fileUniqueId", NormalizeGuid(record.FileUniqueId)), ("$homePage", ToSqlBool(record.HomePage)),
            ("$contentTypeId", record.ContentTypeId), ("$pageType", record.PageType),
            ("$libraryHidden", ToSqlBool(record.LibraryHidden)),
            ("$customizedPageStatusRaw", record.CustomizedPageStatusRaw),
            ("$observationMethod", record.ObservationMethod), ("$selectionState", record.SelectionState),
            ("$welcomePageStatus", record.WelcomePageStatus), ("$scanId", record.ScanId),
            ("$siteUrl", record.SiteUrl), ("$webUrl", record.WebUrl), ("$metadata", metadata));
        Execute(transaction, "INSERT OR IGNORE INTO DiscoveryInventoryObservations (RunId, CanonicalInventoryKey, ObservationId) VALUES ($runId, $key, $observation)",
            ("$runId", runId.ToString("D")), ("$key", canonicalKey), ("$observation", observationId));
    }

    private void UpsertGap(Guid runId, string scopeKey, DiscoverySourceKind sourceKind, string code, string detail,
        string observationKey, SqliteTransaction transaction)
    {
        var gapKey = DiscoveryHash.Of(scopeKey, sourceKind.ToString(), code, observationKey ?? DiscoveryHash.Of(detail ?? string.Empty));
        Execute(transaction, """
            INSERT INTO DiscoveryGaps (RunId, ScopeKey, GapKey, SourceKind, Code, Detail, Resolved)
            VALUES ($runId, $scopeKey, $gapKey, $source, $code, $detail, 0)
            ON CONFLICT(RunId, GapKey) DO UPDATE SET Detail=excluded.Detail, Resolved=0
            """, ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey), ("$gapKey", gapKey),
            ("$source", sourceKind.ToString()), ("$code", code), ("$detail", detail));
    }

    private (string Request, string Response)? ExistingBatch(Guid attemptId, int ordinal, SqliteTransaction transaction)
    {
        using var command = Command("SELECT RequestFingerprint, ResponseFingerprint FROM DiscoveryBatches WHERE AttemptId=$attempt AND BatchOrdinal=$ordinal",
            transaction, ("$attempt", attemptId.ToString("D")), ("$ordinal", ordinal));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private void SetAttempt(Guid attemptId, DiscoveryAttemptStatus status, SqliteTransaction transaction) => Execute(transaction,
        "UPDATE DiscoveryAttempts SET Status=$status, FinishedUtc=$finished WHERE AttemptId=$attempt",
        ("$status", status.ToString()), ("$finished", DateTimeOffset.UtcNow.ToString("O")), ("$attempt", attemptId.ToString("D")));
    private void SetOutcome(Guid runId, string scopeKey, DiscoveryTerminalOutcome outcome, SqliteTransaction transaction) => Execute(transaction,
        "UPDATE DiscoveryScopes SET Outcome=$outcome WHERE RunId=$runId AND ScopeKey=$scopeKey",
        ("$outcome", outcome.ToString()), ("$runId", runId.ToString("D")), ("$scopeKey", scopeKey));

    private void InitializeSchema() => Execute("""
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS DiscoveryRuns (RunId TEXT PRIMARY KEY, ManifestJson TEXT NOT NULL, ManifestHash TEXT NOT NULL, ScopeMode TEXT NOT NULL, ExecutionStatus TEXT NOT NULL, Verdict TEXT NULL, FixtureRun INTEGER NOT NULL, CreatedUtc TEXT NOT NULL, FinishedUtc TEXT NULL);
        CREATE TABLE IF NOT EXISTS DiscoveryScopes (RunId TEXT NOT NULL, ScopeKey TEXT NOT NULL, ParentScopeKey TEXT NULL, Kind TEXT NOT NULL, SourceKind TEXT NULL, Locator TEXT NULL, PermissionContext TEXT NULL, Required INTEGER NOT NULL, Outcome TEXT NOT NULL, ExclusionRuleId TEXT NULL, ExclusionRuleVersion TEXT NULL, ExclusionRuleHash TEXT NULL, ExclusionApprovalRef TEXT NULL, PRIMARY KEY (RunId, ScopeKey));
        CREATE TABLE IF NOT EXISTS DiscoveryChildEnumerations (RunId TEXT NOT NULL, ParentScopeKey TEXT NOT NULL, ChildKind TEXT NOT NULL, Outcome TEXT NOT NULL, ExpectedCount INTEGER NOT NULL, EnumerationFingerprint TEXT NOT NULL, PermissionContext TEXT NULL, UpdatedUtc TEXT NOT NULL, PRIMARY KEY (RunId, ParentScopeKey, ChildKind));
        CREATE TABLE IF NOT EXISTS DiscoveryExpectedChildren (RunId TEXT NOT NULL, ParentScopeKey TEXT NOT NULL, ChildScopeKey TEXT NOT NULL, ChildKind TEXT NOT NULL, SourceKind TEXT NULL, Locator TEXT NULL, PermissionContext TEXT NULL, Required INTEGER NOT NULL, PRIMARY KEY (RunId, ParentScopeKey, ChildScopeKey));
        CREATE TABLE IF NOT EXISTS DiscoveryAttempts (AttemptId TEXT PRIMARY KEY, RunId TEXT NOT NULL, ScopeKey TEXT NOT NULL, SourceKind TEXT NOT NULL, Status TEXT NOT NULL, StartedUtc TEXT NOT NULL, FinishedUtc TEXT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_DiscoveryAttempts_Active ON DiscoveryAttempts(RunId, ScopeKey, SourceKind) WHERE Status='Running';
        CREATE TABLE IF NOT EXISTS DiscoveryBatches (BatchId TEXT PRIMARY KEY, AttemptId TEXT NOT NULL, BatchOrdinal INTEGER NOT NULL, RequestFingerprint TEXT NOT NULL, ResponseFingerprint TEXT NOT NULL, TerminalFlag INTEGER NOT NULL, NextCheckpoint TEXT NULL, CommittedUtc TEXT NOT NULL, UNIQUE (AttemptId, BatchOrdinal));
        CREATE TABLE IF NOT EXISTS DiscoveryObservations (ObservationId TEXT PRIMARY KEY, RunId TEXT NOT NULL, ScopeKey TEXT NOT NULL, SourceKind TEXT NOT NULL, ObservationKey TEXT NOT NULL, FactHash TEXT NOT NULL, SourceObjectKey TEXT NOT NULL, FileName TEXT NULL, PhysicalLocator TEXT NULL, PermissionContext TEXT NULL, MetadataJson TEXT NOT NULL, UNIQUE (RunId, ObservationKey, FactHash));
        CREATE TABLE IF NOT EXISTS DiscoveryAttemptObservations (AttemptId TEXT NOT NULL, ObservationId TEXT NOT NULL, BatchId TEXT NOT NULL, Emitted INTEGER NOT NULL, PRIMARY KEY (AttemptId, ObservationId));
        CREATE TABLE IF NOT EXISTS DiscoveryInventory (
          RunId TEXT NOT NULL, ScopeKey TEXT NOT NULL, CanonicalInventoryKey TEXT NOT NULL,
          PhysicalLocator TEXT NULL, FileName TEXT NOT NULL, IdentityQuality TEXT NOT NULL,
          PermissionContext TEXT NULL, DiscoveryState TEXT NOT NULL, StableSourceObjectKey TEXT NULL,
          SiteCollectionId TEXT NULL, WebId TEXT NULL, ListId TEXT NULL, FolderUniqueId TEXT NULL,
          ListItemId INTEGER NULL, FileUniqueId TEXT NULL, HomePage INTEGER NULL,
          ContentTypeId TEXT NULL, PageType TEXT NULL, LibraryHidden INTEGER NULL,
          CustomizedPageStatusRaw TEXT NULL, ObservationMethod TEXT NULL,
          SelectionState TEXT NULL, WelcomePageStatus TEXT NULL, ScanId TEXT NULL,
          SiteUrl TEXT NULL, WebUrl TEXT NULL, MetadataJson TEXT NOT NULL DEFAULT '{}',
          PRIMARY KEY (RunId, CanonicalInventoryKey));
        CREATE INDEX IF NOT EXISTS IX_DiscoveryInventory_Locator ON DiscoveryInventory(RunId, PhysicalLocator);
        CREATE TABLE IF NOT EXISTS DiscoveryInventoryObservations (RunId TEXT NOT NULL, CanonicalInventoryKey TEXT NOT NULL, ObservationId TEXT NOT NULL, PRIMARY KEY (RunId, CanonicalInventoryKey, ObservationId));
        CREATE TABLE IF NOT EXISTS DiscoveryGaps (RunId TEXT NOT NULL, ScopeKey TEXT NOT NULL, GapKey TEXT NOT NULL, SourceKind TEXT NOT NULL, Code TEXT NOT NULL, Detail TEXT NULL, Resolved INTEGER NOT NULL, PRIMARY KEY (RunId, GapKey));
        CREATE TABLE IF NOT EXISTS DiscoveryConflicts (RunId TEXT NOT NULL, ScopeKey TEXT NOT NULL, ConflictKey TEXT NOT NULL, Code TEXT NOT NULL, ParticipantKeys TEXT NOT NULL, Resolved INTEGER NOT NULL, PRIMARY KEY (RunId, ConflictKey));
        """);

    private static string NormalizeLocator(string locator)
    {
        if (string.IsNullOrWhiteSpace(locator)) return null;
        var normalized = locator.Replace('\\', '/').Trim();
        while (normalized.Contains("//", StringComparison.Ordinal)) normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized.TrimEnd('/').ToLowerInvariant();
    }

    private static string CanonicalInventoryKey(RawDiscoveryRecord record, string normalizedLocator)
    {
        var site = record.SiteCollectionId?.ToString("D");
        var web = record.WebId?.ToString("D");
        var list = record.ListId?.ToString("D");
        var folder = record.FolderUniqueId?.ToString("D");
        if (!string.IsNullOrWhiteSpace(record.FileUniqueId))
        {
            var fileIdentity = Guid.TryParse(record.FileUniqueId, out var fileUniqueId)
                ? fileUniqueId.ToString("D") : record.FileUniqueId.Trim();
            // File identity is scoped by its owning site/web. List/folder provenance must not split
            // one physical file when Forms/Views and folder traversal observe the same object.
            return DiscoveryHash.Of("hierarchical-file", site, web, fileIdentity);
        }
        if (record.ListItemId != null && record.ListId != null)
            return DiscoveryHash.Of("hierarchical-list-item", site, web, list,
                record.ListItemId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return DiscoveryHash.Of("hierarchical-locator", site, web, list, folder,
            record.ContainerStableId, normalizedLocator);
    }

    private static string NormalizeGuid(string value) =>
        Guid.TryParse(value, out var parsed) ? parsed.ToString("D") : null;

    private static object ToSqlBool(bool? value) => value == null ? null : value.Value ? 1 : 0;

    private static Guid? ReadGuid(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || !Guid.TryParse(reader.GetString(ordinal), out var value) ? null : value;

    private static bool? ReadNullableBool(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal) != 0;

    private void Execute(string sql, params (string Name, object Value)[] parameters) => Execute(null, sql, parameters);
    private void Execute(SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        command.ExecuteNonQuery();
    }
    private T Scalar<T>(string sql, params (string Name, object Value)[] parameters) => Scalar<T>(sql, null, parameters);
    private T Scalar<T>(string sql, SqliteTransaction transaction, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        var value = command.ExecuteScalar();
        if (value == null || value == DBNull.Value) return default;
        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
    private IEnumerable<string> Strings(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, null, parameters);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return reader.GetString(0);
    }
    private SqliteCommand Command(string sql, SqliteTransaction transaction, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
