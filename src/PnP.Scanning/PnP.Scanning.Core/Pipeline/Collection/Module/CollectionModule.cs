using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using PnP.Scanning.Core.Pipeline.Collection.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Pipeline.Collection.Module;

/// <summary>
/// Defines a versioned collector producing source observations and recovery checkpoints.
/// </summary>
internal interface ICollectionModule
{
    string ModuleKey { get; }
    string InputVersion { get; }
    /// <summary>
    /// Produces versioned source observations and checkpoints while honoring cancellation.
    /// </summary>
    IAsyncEnumerable<CollectionRecord> CollectAsync(CollectionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Binds fixed collection options, checkpoint, online capabilities, and assessment identity.
/// </summary>
internal sealed record CollectionContext(
    StartRequest Options, VersionedJson Parameters, VersionedJson? Checkpoint,
    ICollectionEnvironment Environment, Guid AssessmentId = default, ICollectionJournal? Journal = null);

/// <summary>
/// Provides bound receipt commits and replay reads for one collection snapshot.
/// </summary>
internal interface ICollectionJournal
{
    /// <summary>
    /// Atomically commits an observation and its collector checkpoint.
    /// </summary>
    Task CommitAsync(CollectionRecord record, CancellationToken cancellationToken);
    /// <summary>
    /// Returns committed receipts, optionally omitting BLOBs for bounded replay indexing.
    /// </summary>
    Task<IReadOnlyList<CollectedObservation>> ReadCommittedAsync(CancellationToken cancellationToken, bool metadataOnly = false);
    /// <summary>
    /// Returns one validated detached receipt from the bound collection snapshot.
    /// </summary>
    Task<CollectedObservation> ReadAsync(Guid observationId, CancellationToken cancellationToken);
}

/// <summary>
/// Validates module-specific source relationships before sealing collection.
/// </summary>
internal interface ICollectionSnapshotValidator
{
    /// <summary>
    /// Validates module-specific source integrity before the snapshot is sealed.
    /// </summary>
    Task ValidateAsync(ICollectionJournal journal, CancellationToken cancellationToken);
}

/// <summary>
/// Restricts protected configuration and authenticated service creation to collection.
/// </summary>
internal interface ICollectionEnvironment
{
    /// <summary>
    /// Clones collection options and protects secrets before persistence.
    /// </summary>
    StartRequest Protect(StartRequest options);
    /// <summary>
    /// Restores protected collection options only when online collection resumes.
    /// </summary>
    StartRequest Restore(StartRequest options);
    /// <summary>
    /// Initializes authentication and resolves online services only for collection execution.
    /// </summary>
    Task<CollectionServices> OpenAsync(StartRequest options, CancellationToken cancellationToken);
}
