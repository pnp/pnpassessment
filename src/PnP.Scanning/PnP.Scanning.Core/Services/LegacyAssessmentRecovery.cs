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
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (Guid assessmentId in store.AssessmentsOnDisk())
        {
            await store.EnsureDatabaseAsync(assessmentId, cancellationToken);
            using ScanContext db = store.CreateContext(assessmentId);
            Storage.Scan scan = await db.Scans.SingleOrDefaultAsync(x => x.ScanId == assessmentId, cancellationToken);
            if (scan == null || await db.PhaseRuns.AnyAsync(x => x.AssessmentId == assessmentId, cancellationToken) ||
                scan.Status != ScanStatus.Running)
                continue;
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
    }

    /// <summary>
    /// Completes shutdown because historical recovery has no ongoing worker.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}