#nullable enable
using System.Threading.Tasks.Dataflow;
using PnP.Scanning.Core.Pipeline.Contracts;

namespace PnP.Scanning.Core.Pipeline.Analysis;

/// <summary>Offline execution. This type accepts only read-only source and result-write capabilities.</summary>
internal sealed class AnalysisExecutor
{
    public async Task ExecuteAsync(IAnalysisModule module, ISnapshotReader reader,
        IAnalysisResultWriter writer, VersionedJson parameters, int threads, CancellationToken cancellationToken)
    {
        var snapshot = await reader.OpenAsync(cancellationToken);
        var completed = await writer.GetCompletedObservationIdsAsync(cancellationToken);
        if (completed.Any(id => !snapshot.ObservationIds.Contains(id)))
            throw new SnapshotIntegrityException("Committed results reference a record outside the selected snapshot.");

        var workers = new ActionBlock<Guid>(async id =>
        {
            var source = await reader.ReadAsync(id, cancellationToken);
            var result = await module.AnalyzeAsync(source, parameters, cancellationToken);
            if (result.ObservationId != id)
                throw new InvalidOperationException("Analyzer returned a result for a different source observation.");
            await writer.WriteAsync(result, cancellationToken);
        }, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = threads,
            BoundedCapacity = Math.Max(1, threads * 2),
            CancellationToken = cancellationToken,
        });

        try
        {
            foreach (var id in snapshot.ObservationIds.Where(id => !completed.Contains(id)))
                if (!await workers.SendAsync(id, cancellationToken)) break;
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
