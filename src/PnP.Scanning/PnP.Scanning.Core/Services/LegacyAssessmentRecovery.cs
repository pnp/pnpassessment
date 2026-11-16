using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;

namespace PnP.Scanning.Core.Services;

/// <summary>
/// Settles interrupted historical assessments without constructing online scan services.
/// </summary>
internal sealed class LegacyAssessmentRecovery(PipelineStore store) : IHostedService
{
    /// <summary>
    /// Terminates interrupted historical running assessments while leaving pipeline recovery to its coordinator.
    /// An assessment whose database cannot be opened or upgraded is logged and kept as it is, so the engine starts
    /// with the assessments that can be read.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (Guid assessmentId in store.AssessmentsOnDisk())
        {
            try
            {
                await TerminateInterruptedAsync(assessmentId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Serilog.Log.Warning(ex, "Assessment {ScanId} could not be read and is left as it is", assessmentId);
            }
        }
    }

    /// <summary>
    /// Marks one historical assessment that was still running when the engine stopped as terminated.
    /// </summary>
    private async Task TerminateInterruptedAsync(Guid assessmentId, CancellationToken cancellationToken)
    {
        await store.EnsureDatabaseAsync(assessmentId, cancellationToken);
        using ScanContext db = store.CreateContext(assessmentId);
        Storage.Scan scan = await db.Scans.SingleOrDefaultAsync(x => x.ScanId == assessmentId, cancellationToken);
        if (scan == null || await db.PhaseRuns.AnyAsync(x => x.AssessmentId == assessmentId, cancellationToken) ||
            scan.Status != ScanStatus.Running)
            return;
        db.History.Add(
            new History
            {
                ScanId = assessmentId,
                Event = Constants.EventAssessmentStatusChange,
                EventDate = DateTime.Now,
                Message = $"From {ScanStatus.Running} to {ScanStatus.Terminated}",
            });
        scan.Status = ScanStatus.Terminated;
        scan.EndDate = DateTime.Now;
        await db.SaveChangesAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(RESTART);", cancellationToken);
    }

    /// <summary>
    /// Completes shutdown because historical recovery has no ongoing worker.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}