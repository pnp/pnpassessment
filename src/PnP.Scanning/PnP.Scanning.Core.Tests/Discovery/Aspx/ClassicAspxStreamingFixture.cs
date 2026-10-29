using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Discovery.Aspx;
using PnP.Scanning.Core.Storage;
using System.Runtime.CompilerServices;

namespace PnP.Scanning.Core.Tests.Discovery.Aspx;

/// <summary>On-demand physical file facts and isolated migrated storage; no SharePoint or source-reader fake is required.</summary>
internal sealed class ClassicAspxStreamingFixture : IAsyncDisposable
{
    internal static readonly Guid SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid WebId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Uri SiteUrl = new("https://example.sharepoint.com/sites/inventory");
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private DbContextOptions<ScanContext> options;
    internal int LargestPublishedBatch { get; private set; }

    /// <summary>Initializes an isolated database using the real generated migrations rather than EnsureCreated.</summary>
    internal async Task InitializeAsync()
    {
        await connection.OpenAsync();
        options = new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options;
        using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    /// <summary>Creates a short-lived context without sharing an EF change tracker across publication batches.</summary>
    internal ScanContext CreateContext() => new(options);

    /// <summary>Returns the fixed observed authority for one synthetic scan.</summary>
    internal static ClassicAspxWebAcquisitionContext Context(Guid scanId, int batchSize = 128) =>
        new(scanId, SiteId, WebId, SiteUrl, SiteUrl, batchSize);

    /// <summary>Creates an observed physical identity whose file GUID and record key are stable across replay.</summary>
    internal static ClassicPageDiscovery Page(Guid scanId, int ordinal) => new()
    {
        ScanId = scanId,
        RecordKey = "folder:file:" + ordinal,
        RowType = "Page",
        SiteCollectionId = SiteId,
        WebId = WebId,
        FileUniqueId = new Guid(ordinal, 0, 0, new byte[8]),
        SiteUrl = SiteUrl.AbsoluteUri,
        WebUrl = "/",
        Url = SiteUrl.AbsoluteUri + "/Pages/file-" + ordinal + ".aspx",
        FileName = "file-" + ordinal + ".aspx",
        DiscoveryStatus = "Discovered",
        ObservationMethod = "Folder.Files",
        ObservedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    /// <summary>Produces file rows only as the consumer requests them; no complete item universe is materialized.</summary>
    internal static async IAsyncEnumerable<ClassicPageDiscovery> Pages(Guid scanId, int count,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int ordinal = 1; ordinal <= count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Page(scanId, ordinal);
        }
        await Task.CompletedTask;
    }

    /// <summary>Persists raw validation gaps before inventory and records the combined publication bound.</summary>
    internal async Task PublishAsync(ClassicAspxAcquisitionBatch batch, CancellationToken cancellationToken)
    {
        LargestPublishedBatch = Math.Max(LargestPublishedBatch, batch.Inventory.Count + batch.Evidence.Count);
        if (batch.Evidence.Count != 0)
        {
            await new AssessmentDiscoveryWriter(CreateContext).WriteAsync(batch.Evidence, cancellationToken);
        }
        await new ClassicAspxProjectionWriter(CreateContext).WriteAsync(batch.Inventory, cancellationToken);
    }

    /// <summary>Disposes the fixture-owned connection after all context and reader lifetimes have ended.</summary>
    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
