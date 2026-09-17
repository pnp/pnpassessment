namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// Lossless raw ASPX inventory entry point. Sources provide unfiltered metadata; hidden/catalog,
/// template, library name, feature, page family, classification and usage are never admission gates.
/// </summary>
internal sealed class AspxDiscoveryRunner
{
    private readonly DiscoveryStore store;
    internal AspxDiscoveryRunner(DiscoveryStore store) => this.store = store;

    internal async Task RunSurfaceAsync(Guid runId, DiscoveryScopeRegistration surface, IRawDiscoverySource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (surface.SourceKind == null) throw new ArgumentException("A surface scope requires a source kind.", nameof(surface));
        store.RegisterScope(runId, surface);
        if (!surface.Required) return;

        var attemptId = store.BeginAttempt(runId, surface.ScopeKey, surface.SourceKind.Value);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var batch in source.ReadBatchesAsync(cancellationToken).WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(batch.NextCheckpoint) && !tokens.Add(batch.NextCheckpoint))
            {
                store.CommitBatch(runId, surface.ScopeKey, surface.SourceKind.Value, attemptId, batch with
                {
                    IsTerminal = true,
                    TerminalOutcome = DiscoveryTerminalOutcome.Unknown,
                    GapCode = DiscoveryGapCodes.PaginationTokenLoopOrLoss,
                    GapDetail = "A continuation token repeated within the effective attempt."
                });
                return;
            }
            var result = store.CommitBatch(runId, surface.ScopeKey, surface.SourceKind.Value, attemptId, batch);
            if (result == BatchCommitResult.ReplayConflict || batch.IsTerminal) return;
        }

        store.CommitBatch(runId, surface.ScopeKey, surface.SourceKind.Value, attemptId,
            new RawDiscoveryBatch(int.MaxValue, DiscoveryHash.Of("implicit-terminal", surface.ScopeKey),
                DiscoveryHash.Of("source-ended-without-terminal", surface.ScopeKey), Array.Empty<RawDiscoveryRecord>(),
                true, DiscoveryTerminalOutcome.Truncated, GapCode: DiscoveryGapCodes.PaginationTokenLoopOrLoss,
                GapDetail: "The source ended without an explicit terminal batch."));
    }
}

internal interface IRawDiscoverySource
{
    IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync(CancellationToken cancellationToken = default);
}

internal sealed record AspxDiscoveryOutputV1(
    string OutputVersion,
    Guid RunId,
    DiscoveryExecutionStatus ExecutionStatus,
    DiscoveryVerdict CoverageVerdict,
    IReadOnlyList<DiscoveryInventoryRow> Inventory,
    IReadOnlyList<DiscoveryObservationRow> Observations,
    IReadOnlyList<DiscoveryCoverageRow> Coverage,
    IReadOnlyList<string> UnresolvedGapCodes,
    int UnresolvedConflictCount)
{
    internal const string Version = "aspx-discovery-output/v1";
    internal static AspxDiscoveryOutputV1 Create(Guid runId, DiscoveryVerdict verdict, DiscoveryStore store) =>
        new(Version, runId, store.ReadExecutionStatus(runId), verdict, store.ReadInventory(runId),
            store.ReadObservations(runId), store.ReadCoverage(runId),
            store.ReadGapCodes(runId), store.ReadConflictCount(runId));
}

internal sealed record AspxDiscoveryOutputV2(
    string OutputVersion,
    Guid RunId,
    DiscoveryExecutionStatus ExecutionStatus,
    DiscoveryVerdict CoverageVerdict,
    IReadOnlyList<DiscoveryInventoryRow> Inventory,
    IReadOnlyList<DiscoveryObservationRow> Observations,
    IReadOnlyList<DiscoveryCoverageRow> Coverage,
    IReadOnlyList<DiscoveryDenominatorRow> Denominator,
    IReadOnlyList<string> UnresolvedGapCodes,
    int UnresolvedConflictCount,
    IReadOnlyList<DiscoveryScopeDetailRow> Scopes = null,
    IReadOnlyList<DiscoveryGapDetailRow> Gaps = null)
{
    internal const string Version = "classic-page-discovery-output/v3";
    internal static AspxDiscoveryOutputV2 Create(Guid runId, DiscoveryVerdict verdict, DiscoveryStore store) =>
        new(Version, runId, store.ReadExecutionStatus(runId), verdict, store.ReadInventory(runId),
            store.ReadObservations(runId), store.ReadCoverage(runId), store.ReadDenominator(runId),
            store.ReadGapCodes(runId), store.ReadConflictCount(runId),
            store.ReadScopes(runId), store.ReadGaps(runId));
}
