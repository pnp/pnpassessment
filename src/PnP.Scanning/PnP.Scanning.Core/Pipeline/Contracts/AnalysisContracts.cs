#nullable enable
namespace PnP.Scanning.Core.Pipeline.Contracts;

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
