#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

internal enum AnalysisOutcome { Analyzed, Unknown, Failed, NotApplicable }

internal sealed record AnalysisResult(Guid ObservationId, AnalysisOutcome Outcome, string Reason, VersionedJson Payload);

internal interface IAnalysisModule
{
    string ModuleKey { get; }
    string RuleVersion { get; }
    Task<AnalysisResult> AnalyzeAsync(SourceRecord source, VersionedJson parameters, CancellationToken cancellationToken);
}

/// <summary>Bound to one analysis run; it has no source-write or online capability.</summary>
internal interface IAnalysisResultWriter
{
    Task<IReadOnlySet<Guid>> GetCompletedObservationIdsAsync(CancellationToken cancellationToken);
    Task WriteAsync(AnalysisResult result, CancellationToken cancellationToken);
}

internal interface IAnalysisResultReader
{
    Task<IReadOnlyList<AnalysisResult>> ReadAsync(CancellationToken cancellationToken);
}

internal interface IAnalysisSnapshotPreparation
{
    Task PrepareAsync(ISnapshotReader snapshot, CancellationToken cancellationToken);
}

internal sealed record AnalysisReportRow(string Kind, string Key, int Ordinal, VersionedJson Payload);

internal interface IAnalysisReportWriter
{
    Task PublishAsync(IReadOnlyList<AnalysisReportRow> rows, CancellationToken cancellationToken);
}

internal interface IAnalysisFinalizer
{
    Task FinalizeAsync(ISnapshotReader snapshot, IAnalysisResultReader results,
        IAnalysisReportWriter reports, VersionedJson parameters, CancellationToken cancellationToken);
}
