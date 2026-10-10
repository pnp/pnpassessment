using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;

namespace PnP.Scanning.Core.Services;

/// <summary>Legacy startup settlement requires local storage, not construction of the online scan engine.</summary>
internal sealed class LegacyAssessmentRecovery(PipelineStore store) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var assessmentId in store.AssessmentsOnDisk())
        {
            await store.EnsureDatabaseAsync(assessmentId, cancellationToken);
            using var db = store.CreateContext(assessmentId);
            var scan = await db.Scans.SingleOrDefaultAsync(x => x.ScanId == assessmentId, cancellationToken);
            if (scan == null || scan.CLIMode?.StartsWith("Pipeline:", StringComparison.Ordinal) == true || scan.Status != ScanStatus.Running) continue;
            db.History.Add(new History
            {
                ScanId = assessmentId, Event = Constants.EventAssessmentStatusChange, EventDate = DateTime.Now,
                Message = $"From {ScanStatus.Running} to {ScanStatus.Terminated}",
            });
            scan.Status = ScanStatus.Terminated;
            scan.EndDate = DateTime.Now;
            await db.SaveChangesAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(RESTART);", cancellationToken);
        }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
