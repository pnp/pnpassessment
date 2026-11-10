using PnP.Scanning.Core.ClassicPages.Collection.Discovery;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Collection.File;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;


namespace PnP.Scanning.Core.ClassicPages.Compatibility;

/// <summary>
/// Classic-assessment acquisition persistence. Network work happens outside the writer gate. A fresh context per
/// commit avoids sharing EF/SQLite state between the existing TPL Web workers.
/// </summary>
internal sealed class AssessmentDiscoveryWriter
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private readonly Func<ScanContext> createContext;
    private readonly Func<IEnumerable<DiscoveryEvidence>, CancellationToken, Task> sourceSink;
    private readonly Func<IEnumerable<PhysicalFileFacts>, CancellationToken, Task> fileSink;

    /// <summary>
    /// Binds the discovery writer to a collection source sink so provider evidence can be captured without legacy table writes.
    /// </summary>
    internal AssessmentDiscoveryWriter(
        Func<IEnumerable<DiscoveryEvidence>, CancellationToken, Task> sourceSink,
        Func<IEnumerable<PhysicalFileFacts>, CancellationToken, Task> fileSink = null)
    {
        this.sourceSink = sourceSink;
        this.fileSink = fileSink;
    }

    internal AssessmentDiscoveryWriter(Guid scanId) : this(() => new ScanContext(scanId))
    {
    }

    internal AssessmentDiscoveryWriter(Func<ScanContext> createContext) => this.createContext = createContext;

    /// <summary>Commits bounded physical facts before ASPX admission, so file-type and later assessment decisions cannot discard raw files.</summary>
    internal async Task WritePhysicalFilesAsync(
        Guid scanId,
        IEnumerable<RawDiscoveryRecord> records,
        CancellationToken cancellationToken)
    {
        foreach (PhysicalFileFacts[] batch in records
                     .Select(record => PhysicalFileProjection.FromPhysicalRecord(scanId, record))
                     .Where(file => file != null)
                     .Chunk(128))
        {
            if (fileSink != null)
                await fileSink(batch, cancellationToken).ConfigureAwait(false);
            else
                await new SPFileWriter(createContext)
                    .WriteAsync(batch.Select(CollectionJson.Convert<SPFile>), cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>Captures native observations directly, adapting only when the sink is legacy persistence.</summary>
    internal Task WriteAsync(IEnumerable<DiscoveryEvidence> rows, CancellationToken cancellationToken = default) =>
        sourceSink != null
            ? sourceSink(rows, cancellationToken)
            : WriteAsync(rows.Select(DiscoveryEvidenceMapper.ToLegacy), cancellationToken);

    /// <summary>Persists raw observations without erasing retained failures or rekeying preview evidence during resume.</summary>
    internal async Task WriteAsync(
        IEnumerable<ClassicPageDiscovery> rows,
        CancellationToken cancellationToken = default)
    {
        if (sourceSink != null)
        {
            await sourceSink(rows.Select(DiscoveryEvidenceMapper.FromLegacy), cancellationToken);
            return;
        }

        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using ScanContext db = createContext();
            foreach (ClassicPageDiscovery row in rows)
            {
                ClassicPageDiscovery previous = await db.ClassicPageDiscoveries.FindAsync(
                        new object[] { row.ScanId, row.RecordKey },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (previous == null)
                    db.ClassicPageDiscoveries.Add(row);
                else
                {
                    DiscoveryEvidence incoming = DiscoveryEvidenceMapper.FromLegacy(row);
                    DiscoveryEvidenceRules.Reconcile(DiscoveryEvidenceMapper.FromLegacy(previous), incoming);
                    db.Entry(previous).CurrentValues.SetValues(DiscoveryEvidenceMapper.ToLegacy(incoming));
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>Streams ordered scan/web page observations in bounded keyset batches, disposing each database reader before yielding rows to acquisition.</summary>
    internal async IAsyncEnumerable<ClassicPageDiscovery> ReadPageStreamAsync(
        Guid scanId,
        string siteUrl,
        string webUrl,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string after = null;
        while (true)
        {
            List<ClassicPageDiscovery> batch;
            using (var db = createContext())
            {
                batch = await db.ClassicPageDiscoveries.AsNoTracking()
                    .Where(row =>
                        row.ScanId == scanId && row.SiteUrl == siteUrl && row.WebUrl == webUrl &&
                        row.RowType == "Page" && (after == null || string.Compare(row.RecordKey, after) > 0))
                    .OrderBy(row => row.RecordKey)
                    .Take(128)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            if (batch.Count == 0)
            {
                yield break;
            }

            after = batch[^1].RecordKey;
            // No live SQLite reader is held while a source read or a projection write awaits.
            foreach (var row in batch)
            {
                yield return row;
            }
        }
    }

    /// <summary>Streams persisted Web discovery roots across the whole scan without holding a SQLite reader during acquisition.</summary>
    internal async IAsyncEnumerable<ClassicPageDiscovery> ReadDiscoveredWebScopesAsync(
        Guid scanId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string after = null;
        while (true)
        {
            List<ClassicPageDiscovery> batch;
            using (var db = createContext())
            {
                batch = await db.ClassicPageDiscoveries.AsNoTracking()
                    .Where(row => row.ScanId == scanId && row.RowType == "Scope" && row.ScopeType == "Web" &&
                                  row.ParentScopeKey == null && row.SiteCollectionId != null && row.WebId != null &&
                                  row.RecordKey.StartsWith("scope:") &&
                                  (after == null || string.Compare(row.RecordKey, after) > 0))
                    .OrderBy(row => row.RecordKey)
                    .Take(128)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            if (batch.Count == 0) yield break;
            after = batch[^1].RecordKey;
            foreach (var row in batch) yield return row;
        }
    }

    internal async Task<List<ClassicPageDiscovery>> ReadPagesAsync(
        Guid scanId,
        string siteUrl,
        string webUrl,
        CancellationToken cancellationToken = default)
    {
        using var db = createContext();
        return await db.ClassicPageDiscoveries.AsNoTracking()
            .Where(row =>
                row.ScanId == scanId && row.SiteUrl == siteUrl && row.WebUrl == webUrl && row.RowType == "Page")
            .OrderBy(row => row.RecordKey)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task UpdateExistingAsync(
        IEnumerable<ClassicPageDiscovery> rows,
        CancellationToken cancellationToken = default)
    {
        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var db = createContext();
            db.ClassicPageDiscoveries.UpdateRange(rows);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    internal async Task<int> FailUnassessedPagesAsync(
        Guid scanId,
        string siteUrl,
        string webUrl,
        Exception error,
        string stage)
    {
        // This is failure finalization, not discovery: retain file identities and existence,
        // and never overwrite pages that already reached an assessment disposition.
        await WriteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var db = createContext();
            var unfinished = await db.ClassicPageDiscoveries.Where(row => row.ScanId == scanId &&
                                                                          row.SiteUrl == siteUrl &&
                                                                          row.WebUrl == webUrl &&
                                                                          row.RowType == "Page" &&
                                                                          (row.AssessmentStatus == null ||
                                                                           row.AssessmentStatus == ""))
                .ToListAsync()
                .ConfigureAwait(false);
            foreach (var row in unfinished)
            {
                row.AssessmentStatus = "Failed";
                AssessmentWebDiscovery.AddError(
                    row,
                    stage,
                    AssessmentWebDiscovery.ErrorCode(error),
                    "Page assessment did not finish because its Web failed: " + error.GetBaseException().Message);
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
            return unfinished.Count;
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>
    /// Computes and persists assessment coverage from retained discovery evidence.
    /// </summary>
    internal async Task<DiscoveryVerdict> FinalizeScanAsync(Guid scanId, CancellationToken cancellationToken = default)
    {
        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using ScanContext db = createContext();
            List<ClassicPageDiscovery> allRows = await db.ClassicPageDiscoveries.AsNoTracking()
                .Where(row => row.ScanId == scanId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            ClassicPageDiscovery current = DiscoveryEvidenceMapper.ToLegacy(
                DiscoveryEvidenceRules.BuildCoverage(
                    scanId,
                    allRows.Select(DiscoveryEvidenceMapper.FromLegacy).ToArray(),
                    DateTime.UtcNow));
            DiscoveryVerdict verdict = Enum.Parse<DiscoveryVerdict>(current.DiscoveryStatus);
            ClassicPageDiscovery summary = await db.ClassicPageDiscoveries.FindAsync(
                    new object[] { scanId, "summary:coverage" },
                    cancellationToken)
                .ConfigureAwait(false);
            if (summary == null)
                db.ClassicPageDiscoveries.Add(current);
            else
                db.Entry(summary).CurrentValues.SetValues(current);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return verdict;
        }
        finally
        {
            WriteGate.Release();
        }
    }

    internal async Task<string> ReadWebCoverageAsync(Guid scanId, string siteUrl, string webUrl)
    {
        using var db = createContext();
        var incomplete = await db.ClassicPageDiscoveries.AsNoTracking()
            .AnyAsync(row => row.ScanId == scanId && row.SiteUrl == siteUrl && row.WebUrl == webUrl &&
                             (row.RowType == "Scope" || row.RowType == "Gap") &&
                             (row.ObservationMethod == null || row.ObservationMethod != "WebScan") &&
                             row.DiscoveryStatus != "Complete" && row.DiscoveryStatus != "Empty" &&
                             row.DiscoveryStatus != "PolicyExcluded");
        return incomplete ? "Partial" : "Complete";
    }
}