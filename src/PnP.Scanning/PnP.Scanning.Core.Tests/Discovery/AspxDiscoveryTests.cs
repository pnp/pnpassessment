using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed class AspxDiscoveryTests
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public async Task Raw_inventory_keeps_hidden_custom_uppercase_and_classification_failure_pages()
    {
        using var database = new TemporaryDatabase();
        using var store = new DiscoveryStore(database.Path);
        var runId = CreateRun(store);
        var surface = Surface("tenant/geo/site/web/library");
        var records = new[]
        {
            Record("F05", "/hidden/kept.ASPX", "kept.ASPX", new Dictionary<string, string> { ["hidden"] = "true" }),
            Record("F10", "/custom/also-kept.aspx", "also-kept.aspx", new Dictionary<string, string> { ["libraryTemplate"] = "custom" }),
            Record("F12", "/classify/failure.aspx", "failure.aspx", new Dictionary<string, string> { ["classificationError"] = "synthetic" }),
            Record("N01", "/backup/not-a-page.aspx.bak", "not-a-page.aspx.bak")
        };

        await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, surface, Source(Batch(0, records, true)));
        store.FinishExecution(runId, DiscoveryExecutionStatus.Finished);

        store.ReadInventory(runId).Select(row => row.FileName)
            .Should().BeEquivalentTo("kept.ASPX", "also-kept.aspx", "failure.aspx");
        store.EvaluateVerdict(runId, false).Should().Be(DiscoveryVerdict.CompleteDeclaredSubset);
        store.GetCounts(runId, surface.ScopeKey).Should().Be(new DiscoveryCounts(4, 3, 3, 1, 1, 0, 0));
    }

    [Fact]
    public async Task Missing_name_is_gap_and_locator_fallback_requires_physical_path_guarantee()
    {
        using var database = new TemporaryDatabase();
        using var store = new DiscoveryStore(database.Path);
        var runId = CreateRun(store);
        var surface = Surface("web/forms", DiscoverySourceKind.ListFormBackingFiles);
        var missing = new RawDiscoveryRecord("F25", null, "forms", null, "/forms/Nameless.aspx", false, "fixture");
        var fallback = new RawDiscoveryRecord("F24", null, "forms", null, "/forms/Fallback.aspx", true, "fixture");

        await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, surface, Source(Batch(0, new[] { missing, fallback }, true)));

        store.ReadInventory(runId).Should().ContainSingle(row => row.FileName == "Fallback.aspx" && row.IdentityQuality == "fallback");
        store.ReadGapCodes(runId).Should().ContainSingle(code => code == DiscoveryGapCodes.FilenameMissing);
        store.EvaluateVerdict(runId, false).Should().Be(DiscoveryVerdict.Unknown);
    }

    [Fact]
    public async Task Duplicate_source_deduplicates_inventory_but_locator_identity_conflict_keeps_both()
    {
        using var database = new TemporaryDatabase();
        using var store = new DiscoveryStore(database.Path);
        var runId = CreateRun(store);
        await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, Surface("web/library"),
            Source(Batch(0, new[] { Record("F15", "/forms/EditForm.aspx", "EditForm.aspx") }, true)));
        await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, Surface("web/forms", DiscoverySourceKind.ListFormBackingFiles),
            Source(Batch(0, new[] { Record("F15", "/forms/EditForm.aspx", "EditForm.aspx") }, true)));
        store.ReadInventory(runId).Should().ContainSingle();

        await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, Surface("web/conflict"), Source(Batch(0, new[]
        {
            Record("F21", "/Conflict/Same.aspx", "Same.aspx"),
            Record("F22", "/Conflict/Same.aspx", "Same.aspx")
        }, true)));

        store.ReadInventory(runId).Should().HaveCount(3);
        store.ReadConflictCount(runId).Should().Be(1);
        store.EvaluateVerdict(runId, false).Should().Be(DiscoveryVerdict.Unknown);
    }

    [Fact]
    public async Task Pagination_counts_terminal_empty_batch_and_supports_more_than_1000_rows()
    {
        using var database = new TemporaryDatabase();
        using var store = new DiscoveryStore(database.Path);
        var runId = CreateRun(store);
        var surface = Surface("web/big");
        RawDiscoveryRecord[] Page(int start, int count) => Enumerable.Range(start, count)
            .Select(index => Record($"A{index}", $"/big/A{index}.aspx", $"A{index}.aspx")).ToArray();

        await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, surface, Source(
            Batch(0, Page(0, 500), false, "token-1"),
            Batch(1, Page(500, 500), false, "token-2"),
            Batch(2, Page(1000, 5), false, "token-3"),
            Batch(3, Array.Empty<RawDiscoveryRecord>(), true)));

        store.GetCounts(runId, surface.ScopeKey).Should().Be(new DiscoveryCounts(1005, 1005, 1005, 4, 1, 0, 0));
    }

    [Fact]
    public void Batch_replay_is_idempotent_and_changed_fingerprint_fails_closed()
    {
        using var database = new TemporaryDatabase();
        using var store = new DiscoveryStore(database.Path);
        var runId = CreateRun(store);
        var surface = Surface("web/replay");
        store.RegisterScope(runId, surface);
        var attempt = store.BeginAttempt(runId, surface.ScopeKey, surface.SourceKind.Value);
        var batch = Batch(0, new[] { Record("F01", "/a.aspx", "a.aspx") }, false, "next");

        store.CommitBatch(runId, surface.ScopeKey, surface.SourceKind.Value, attempt, batch).Should().Be(BatchCommitResult.Committed);
        store.CommitBatch(runId, surface.ScopeKey, surface.SourceKind.Value, attempt, batch).Should().Be(BatchCommitResult.ReplayNoOp);
        store.GetCounts(runId, surface.ScopeKey).Should().Be(new DiscoveryCounts(1, 1, 1, 1, 1, 0, 0));
        store.CommitBatch(runId, surface.ScopeKey, surface.SourceKind.Value, attempt,
            batch with { ResponseFingerprint = DiscoveryHash.Of("different") }).Should().Be(BatchCommitResult.ReplayConflict);

        store.ReadGapCodes(runId).Should().Contain(DiscoveryGapCodes.BatchReplayConflict);
        store.EvaluateVerdict(runId, false).Should().Be(DiscoveryVerdict.Unknown);
    }

    [Theory]
    [InlineData(DiscoveryTerminalOutcome.Denied, null, DiscoveryVerdict.Incomplete)]
    [InlineData(DiscoveryTerminalOutcome.Failed, null, DiscoveryVerdict.Incomplete)]
    [InlineData(DiscoveryTerminalOutcome.Truncated, null, DiscoveryVerdict.Incomplete)]
    [InlineData(DiscoveryTerminalOutcome.Unknown, DiscoveryGapCodes.SourceUnsupported, DiscoveryVerdict.Unknown)]
    [InlineData(DiscoveryTerminalOutcome.Unknown, DiscoveryGapCodes.PermissionVisibilityUnknown, DiscoveryVerdict.Unknown)]
    public async Task Failure_permission_and_unsupported_outcomes_cannot_be_complete(
        DiscoveryTerminalOutcome outcome, string gapCode, DiscoveryVerdict expected)
    {
        using var database = new TemporaryDatabase();
        using var store = new DiscoveryStore(database.Path);
        var runId = CreateRun(store);
        await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, Surface("web/fault"), Source(
            new RawDiscoveryBatch(0, "request", "response", Array.Empty<RawDiscoveryRecord>(), true, outcome, GapCode: gapCode)));
        store.FinishExecution(runId, DiscoveryExecutionStatus.Finished);
        store.EvaluateVerdict(runId, false).Should().Be(expected);
    }

    [Fact]
    public void Resume_requires_complete_immutable_provenance_and_rejects_drift()
    {
        var baseline = Manifest();
        ResumeDecision.Evaluate(baseline, baseline, true).CanResume.Should().BeTrue();
        var drifted = baseline with { EnvironmentManifestHash = new string('b', 64) };
        var decision = ResumeDecision.Evaluate(baseline, drifted, true);
        decision.CanResume.Should().BeFalse();
        decision.MismatchFields.Should().Contain(nameof(DiscoveryRunManifest.EnvironmentManifestHash));
        ResumeDecision.Evaluate(baseline, baseline with { ProductRef = "pnp/pnpassessment@main" }, true).CanResume.Should().BeFalse();
    }

    [Fact]
    public async Task Versioned_exclusion_caps_verdict_below_tenant_verified()
    {
        using var database = new TemporaryDatabase();
        using var store = new DiscoveryStore(database.Path);
        var runId = CreateRun(store);
        await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, Surface("web/raw"),
            Source(Batch(0, Array.Empty<RawDiscoveryRecord>(), true)));
        store.RegisterScope(runId, new DiscoveryScopeRegistration("web/forms", null, DiscoveryScopeKind.Container,
            DiscoverySourceKind.ListFormBackingFiles, "/forms", "fixture", Required: false,
            ExclusionRuleId: "rule-1", ExclusionRuleVersion: "1", ExclusionRuleHash: Hash,
            ExclusionApprovalRef: "CCD-fixture"));
        store.EvaluateVerdict(runId, true).Should().Be(DiscoveryVerdict.CompleteDeclaredSubset);
    }

    [Fact]
    public async Task Scope_ledger_and_output_bind_tenant_to_folder_provenance_and_separate_execution_from_coverage()
    {
        using var database = new TemporaryDatabase();
        using var store = new DiscoveryStore(database.Path);
        var runId = CreateRun(store);
        var scopes = new[]
        {
            new DiscoveryScopeRegistration("tenant", null, DiscoveryScopeKind.Tenant, null, "tenant", "authorized"),
            new DiscoveryScopeRegistration("tenant/geo", "tenant", DiscoveryScopeKind.Geo, null, "geo", "authorized"),
            new DiscoveryScopeRegistration("tenant/geo/site", "tenant/geo", DiscoveryScopeKind.SiteCollection, null, "/site", "authorized"),
            new DiscoveryScopeRegistration("tenant/geo/site/web", "tenant/geo/site", DiscoveryScopeKind.Web, null, "/site/web", "authorized"),
            new DiscoveryScopeRegistration("tenant/geo/site/web/library", "tenant/geo/site/web", DiscoveryScopeKind.Container,
                DiscoverySourceKind.RawListLibraryFiles, "/library", "authorized"),
            new DiscoveryScopeRegistration("tenant/geo/site/web/library/folder", "tenant/geo/site/web/library", DiscoveryScopeKind.Folder,
                DiscoverySourceKind.RawListLibraryFiles, "/library/folder", "authorized")
        };
        foreach (var scope in scopes) store.RegisterScope(runId, scope);
        foreach (var scope in scopes.Take(5)) store.RecordScopeOutcome(runId, scope.ScopeKey, DiscoveryTerminalOutcome.Complete);
        await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, scopes[^1],
            Source(Batch(0, new[] { Record("F01", "/library/folder/Page.aspx", "Page.aspx") }, true)));
        store.FinishExecution(runId, DiscoveryExecutionStatus.Finished);
        var verdict = store.EvaluateVerdict(runId, tenantVisibilityVerified: true);
        var output = AspxDiscoveryOutputV1.Create(runId, verdict, store);

        output.OutputVersion.Should().Be(AspxDiscoveryOutputV1.Version);
        output.ExecutionStatus.Should().Be(DiscoveryExecutionStatus.Finished);
        output.CoverageVerdict.Should().Be(DiscoveryVerdict.CompleteDeclaredSubset);
        output.Coverage.Select(row => row.Kind).Should().Contain(new[] { "Tenant", "Geo", "SiteCollection", "Web", "Container", "Folder" });
        output.Coverage.Should().OnlyContain(row => row.ExpectedCount == null);
        output.Observations.Should().ContainSingle(row => row.PermissionContext == "synthetic-authorized");
    }

    [Fact]
    public async Task Interrupted_attempt_replays_from_container_start_without_multiplying_inventory_or_effective_counts()
    {
        using var database = new TemporaryDatabase();
        using var store = new DiscoveryStore(database.Path);
        var runId = CreateRun(store);
        var surface = Surface("web/resume");
        store.RegisterScope(runId, surface);
        var firstAttempt = store.BeginAttempt(runId, surface.ScopeKey, surface.SourceKind.Value);
        store.CommitBatch(runId, surface.ScopeKey, surface.SourceKind.Value, firstAttempt,
            Batch(0, new[] { Record("F01", "/resume/a.aspx", "a.aspx") }, false, "token-1"));

        await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, surface, Source(
            Batch(0, new[] { Record("F01", "/resume/a.aspx", "a.aspx") }, false, "token-1"),
            Batch(1, new[] { Record("F02", "/resume/b.aspx", "b.aspx") }, true)));

        store.ReadInventory(runId).Should().HaveCount(2);
        store.GetCounts(runId, surface.ScopeKey).Should().Be(new DiscoveryCounts(2, 2, 2, 2, 2, 0, 0));
    }

    [Fact]
    public async Task Duplicate_continuation_token_is_persisted_as_unknown_gap()
    {
        using var database = new TemporaryDatabase();
        using var store = new DiscoveryStore(database.Path);
        var runId = CreateRun(store);
        await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, Surface("web/token-loop"), Source(
            Batch(0, new[] { Record("F01", "/loop/a.aspx", "a.aspx") }, false, "same-token"),
            Batch(1, new[] { Record("F02", "/loop/b.aspx", "b.aspx") }, false, "same-token")));

        store.ReadGapCodes(runId).Should().Contain(DiscoveryGapCodes.PaginationTokenLoopOrLoss);
        store.EvaluateVerdict(runId, false).Should().Be(DiscoveryVerdict.Unknown);
    }

    [Fact]
    public async Task Retained_fresh_sqlite_evidence_reopens_with_independent_expected_inventory_set()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aspx-discovery-retained-evidence.sqlite");
        if (File.Exists(path)) File.Delete(path);
        var expected = new[] { "Custom.aspx", "Upper.ASPX" };
        Guid runId;
        using (var store = new DiscoveryStore(path))
        {
            runId = CreateRun(store);
            await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, Surface("web/retained"), Source(Batch(0, new[]
            {
                Record("F01", "/retained/Custom.aspx", "Custom.aspx"),
                Record("F02", "/retained/Upper.ASPX", "Upper.ASPX"),
                Record("N01", "/retained/Backup.aspx.bak", "Backup.aspx.bak")
            }, true)));
            store.FinishExecution(runId, DiscoveryExecutionStatus.Finished);
            store.EvaluateVerdict(runId, false).Should().Be(DiscoveryVerdict.CompleteDeclaredSubset);
        }
        using (var reopened = new DiscoveryStore(path))
        {
            reopened.ReadInventory(runId).Select(row => row.FileName).Should().BeEquivalentTo(expected);
        }
    }

    private static Guid CreateRun(DiscoveryStore store)
    {
        var runId = Guid.NewGuid();
        store.CreateRun(runId, Manifest(), "declared_subset", true);
        return runId;
    }

    private static DiscoveryRunManifest Manifest() => new(
        "pnp/pnpassessment@1a5bd01b2cb84aec7334668e95eaf7023bfffe77",
        "1f07296b186698c3cc9ca8580f00af36c0f3f4f5",
        DiscoveryRunManifest.CurrentContractVersion, DiscoveryRunManifest.CurrentSchemaVersion,
        Hash, Hash, Hash, Hash, Hash, Hash, Hash, Hash, "synthetic-fixtures/v1", Hash);

    private static DiscoveryScopeRegistration Surface(string key,
        DiscoverySourceKind sourceKind = DiscoverySourceKind.RawListLibraryFiles) =>
        new(key, null, DiscoveryScopeKind.Container, sourceKind, "/" + key, "synthetic-authorized");
    private static RawDiscoveryRecord Record(string id, string locator, string fileName,
        IReadOnlyDictionary<string, string> metadata = null) =>
        new(id, id, "container", fileName, locator, true, "synthetic-authorized", metadata);
    private static RawDiscoveryBatch Batch(int ordinal, IReadOnlyList<RawDiscoveryRecord> records, bool terminal, string next = null) =>
        new(ordinal, DiscoveryHash.Of("request", ordinal.ToString()), DiscoveryHash.Of("response", ordinal.ToString()),
            records, terminal, records.Count == 0 ? DiscoveryTerminalOutcome.Empty : DiscoveryTerminalOutcome.Complete, next);
    private static IRawDiscoverySource Source(params RawDiscoveryBatch[] batches) => new SyntheticSource(batches);

    private sealed class SyntheticSource : IRawDiscoverySource
    {
        private readonly IReadOnlyList<RawDiscoveryBatch> batches;
        internal SyntheticSource(IReadOnlyList<RawDiscoveryBatch> batches) => this.batches = batches;
        public async IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return batch;
                await Task.Yield();
            }
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "aspx-discovery-" + Guid.NewGuid().ToString("N") + ".sqlite");
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }
}
