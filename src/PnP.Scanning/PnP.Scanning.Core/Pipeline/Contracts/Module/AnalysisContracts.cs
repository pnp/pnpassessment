#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

/// <summary>
/// Defines explicit analyzed, unknown, failed and not-applicable outcomes for one saved source record.
/// </summary>
internal enum AnalysisOutcome { Analyzed, Unknown, Failed, NotApplicable }

/// <summary>
/// Carries the outcome, reason and versioned payload produced for one observation in an analysis run.
/// </summary>
internal sealed record AnalysisResult(Guid ObservationId, AnalysisOutcome Outcome, string Reason, VersionedJson Payload);

/// <summary>
/// Defines a rule-versioned offline analyzer that consumes detached source records and returns per-record results.
/// </summary>
internal interface IAnalysisModule
{
    string ModuleKey { get; }
    string RuleVersion { get; }
    /// <summary>
    /// Evaluates one saved source using the fixed analysis parameters and returns an explicit result without online access.
    /// </summary>
    Task<AnalysisResult> AnalyzeAsync(SourceRecord source, VersionedJson parameters, CancellationToken cancellationToken);
}

/// <summary>
/// Provides append-only result writes and committed-member reads for one analysis run.
/// </summary>
internal interface IAnalysisResultWriter
{
    /// <summary>
    /// Returns observations with committed results so restart can skip completed work.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetCompletedObservationIdsAsync(CancellationToken cancellationToken);
    /// <summary>
    /// Commits a result for one fixed snapshot member without allowing collected source data to be changed.
    /// </summary>
    Task WriteAsync(AnalysisResult result, CancellationToken cancellationToken);
}

/// <summary>
/// Provides read-only access to the committed per-source results of one analysis run.
/// </summary>
internal interface IAnalysisResultReader
{
    /// <summary>
    /// Reads the committed results needed to finalize the selected run.
    /// </summary>
    Task<IReadOnlyList<AnalysisResult>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Allows an analyzer to prepare shared indexes from the sealed snapshot before parallel record evaluation.
/// </summary>
internal interface IAnalysisSnapshotPreparation
{
    /// <summary>
    /// Validates and prepares read-only shared snapshot state before individual observations are analyzed.
    /// </summary>
    Task PrepareAsync(ISnapshotReader snapshot, CancellationToken cancellationToken);
}

/// <summary>
/// Carries one report projection with its report kind, stable row key, ordering and versioned payload.
/// </summary>
internal sealed record AnalysisReportRow(string Kind, string Key, int Ordinal, VersionedJson Payload);

/// <summary>
/// Publishes a fixed set of report rows for the current analysis run after its source results are committed.
/// </summary>
internal interface IAnalysisReportWriter
{
    /// <summary>
    /// Publishes deterministic report projections without replacing previously published rows.
    /// </summary>
    Task PublishAsync(IReadOnlyList<AnalysisReportRow> rows, CancellationToken cancellationToken);
}

/// <summary>
/// Defines offline report assembly after the analyzer has produced results for the selected snapshot members.
/// </summary>
internal interface IAnalysisFinalizer
{
    /// <summary>
    /// Combines fixed source metadata and committed results into run-scoped report rows.
    /// </summary>
    Task FinalizeAsync(ISnapshotReader snapshot, IAnalysisResultReader results,
        IAnalysisReportWriter reports, VersionedJson parameters, CancellationToken cancellationToken);
}
