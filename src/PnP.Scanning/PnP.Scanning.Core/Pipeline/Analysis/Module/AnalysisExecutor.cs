#nullable enable
using System.Threading.Tasks.Dataflow;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Analysis.Module;

/// <summary>
/// Evaluates the pinned snapshot with bounded concurrency and append-only result writes.
/// </summary>
internal sealed class AnalysisExecutor
{
    /// <summary>
    /// Evaluates uncommitted members of the pinned snapshot with bounded parallelism and validates completion.
    /// </summary>
    public async Task ExecuteAsync(
        IAnalysisModule module,
        ISnapshotReader reader,
        IAnalysisResultWriter writer,
        VersionedJson parameters,
        int threads,
        CancellationToken cancellationToken)
    {
        SnapshotInfo? snapshot = await reader.OpenAsync(cancellationToken);
        if (module is IAnalysisSnapshotPreparation preparation)
            await preparation.PrepareAsync(reader, cancellationToken);
        IReadOnlySet<Guid>? completed = await writer.GetCompletedObservationIdsAsync(cancellationToken);
        if (completed.Any(id => !snapshot.ObservationIds.Contains(id)))
            throw new SnapshotIntegrityException("Committed results reference a record outside the selected snapshot.");

        ActionBlock<Guid>? workers = new ActionBlock<Guid>(
            async id =>
            {
                SourceRecord? source = await reader.ReadAsync(id, cancellationToken);
                AnalysisResult? result = await module.AnalyzeAsync(source, parameters, cancellationToken);
                if (result.ObservationId != id)
                    throw new InvalidOperationException(
                        "Analyzer returned a result for a different source observation.");
                await writer.WriteAsync(result, cancellationToken);
            },
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = threads,
                BoundedCapacity = Math.Max(1, threads * 2),
                CancellationToken = cancellationToken,
            });

        try
        {
            foreach (Guid id in snapshot.ObservationIds.Where(id => !completed.Contains(id)))
                if (!await workers.SendAsync(id, cancellationToken))
                    break;
        }
        finally
        {
            workers.Complete();
            await workers.Completion;
        }

        // Detect changes even to already committed records before settling the run as Finished.
        await reader.OpenAsync(cancellationToken);
    }
}