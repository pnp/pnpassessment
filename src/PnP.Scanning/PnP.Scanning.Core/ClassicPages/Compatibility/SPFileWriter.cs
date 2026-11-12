using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.ClassicPages.Compatibility;

/// <summary>Atomically merges bounded physical-file observations without inventing identities or discarding conflicting evidence.</summary>
internal sealed class SPFileWriter
{
    internal const int MaximumBatchSize = 256;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly Func<ScanContext> createContext;

    /// <summary>Creates a writer whose independently owned contexts target the specified assessment scan.</summary>
    internal SPFileWriter(Guid scanId) : this(() => new ScanContext(scanId))
    {
    }

    /// <summary>Retains the context factory used to isolate each serialized transactional batch.</summary>
    internal SPFileWriter(Func<ScanContext> createContext)
    {
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
    }

    /// <summary>Validates one bounded single-scan raw-file batch, unions repeat evidence, and commits all observations or none.</summary>
    internal async Task WriteAsync(IEnumerable<SPFile> observations, CancellationToken cancellationToken = default)
    {
        SPFile[] files = Bounded(observations);
        if (files.Length == 0)
        {
            return;
        }

        foreach (SPFile file in files)
        {
            PhysicalFileConsolidator.Validate(CollectionJson.Convert<PhysicalFileFacts>(file));
        }

        Guid scan = files[0].ScanId;
        if (files.Any(row => row.ScanId != scan))
        {
            throw new ArgumentException("A raw-file batch belongs to exactly one scan.");
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using ScanContext db = createContext();
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (SPFile observation in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SPFile previous = await db.SPFiles.FindAsync(
                        new object[] { observation.SiteCollectionId, observation.FileUniqueId },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (previous != null && previous.ScanId != observation.ScanId)
                    throw new InvalidOperationException("The physical file already belongs to a different scan.");
                PhysicalFileFacts merged = PhysicalFileConsolidator.Consolidate(
                    previous == null ? null : CollectionJson.Convert<PhysicalFileFacts>(previous),
                    CollectionJson.Convert<PhysicalFileFacts>(observation));
                SPFile stored = CollectionJson.Convert<SPFile>(merged);
                if (previous == null)
                    db.SPFiles.Add(stored);
                else
                    db.Entry(previous).CurrentValues.SetValues(stored);
            }

            // Storage failures propagate; no part of the batch is reported as an acquisition gap or committed separately.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Reads at most one batch plus a sentinel and rejects oversized input before creating a database context.</summary>
    private static SPFile[] Bounded(IEnumerable<SPFile> rows)
    {
        SPFile[] result = (rows ?? Array.Empty<SPFile>()).Take(MaximumBatchSize + 1).ToArray();
        if (result.Length > MaximumBatchSize)
        {
            throw new ArgumentException("Raw-file batches must remain bounded.");
        }

        return result;
    }
}