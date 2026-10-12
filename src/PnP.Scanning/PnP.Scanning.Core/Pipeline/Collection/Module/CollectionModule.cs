#nullable enable
using PnP.Scanning.Core.Pipeline.Collection.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Pipeline.Collection.Module;

/// <summary>
/// Defines a versioned collector that emits source observations and durable progress checkpoints.
/// </summary>
internal interface ICollectionModule
{
    string ModuleKey { get; }
    string InputVersion { get; }
    /// <summary>
    /// Produces raw collection records using only the supplied collection context and observes cancellation.
    /// </summary>
    IAsyncEnumerable<CollectionRecord> CollectAsync(CollectionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Binds fixed options, parameters and a restore checkpoint to the journal and online services available during collection.
/// </summary>
internal sealed record CollectionContext(
    StartRequest Options, VersionedJson Parameters, VersionedJson? Checkpoint,
    ICollectionEnvironment Environment, Guid AssessmentId = default, ICollectionJournal? Journal = null);

/// <summary>
/// Provides atomic source commits and reads of existing receipts for one unsealed collection snapshot.
/// </summary>
internal interface ICollectionJournal
{
    /// <summary>
    /// Commits one observation and its checkpoint together, or advances only the checkpoint when no observation is supplied.
    /// </summary>
    Task CommitAsync(CollectionRecord record, CancellationToken cancellationToken);
    /// <summary>
    /// Lists saved observations for resume, optionally projecting metadata without loading their BLOBs.
    /// </summary>
    Task<IReadOnlyList<CollectedObservation>> ReadCommittedAsync(CancellationToken cancellationToken, bool metadataOnly = false);
    /// <summary>
    /// Reads and validates one previously committed collection observation, including its saved content.
    /// </summary>
    Task<CollectedObservation> ReadAsync(Guid observationId, CancellationToken cancellationToken);
}

/// <summary>
/// Allows a collector to check module-specific source consistency before infrastructure seals its snapshot.
/// </summary>
internal interface ICollectionSnapshotValidator
{
    /// <summary>
    /// Checks the completed collection receipts before publishing a sealed input snapshot.
    /// </summary>
    Task ValidateAsync(ICollectionJournal journal, CancellationToken cancellationToken);
}

/// <summary>
/// Supplies protected configuration and authenticated services only to collection execution.
/// </summary>
internal interface ICollectionEnvironment
{
    /// <summary>
    /// Returns persistable collection options with any secret fields protected.
    /// </summary>
    StartRequest Protect(StartRequest options);
    /// <summary>
    /// Restores protected options when the original collection resumes.
    /// </summary>
    StartRequest Restore(StartRequest options);
    /// <summary>
    /// Initializes the authenticated services required for online collection.
    /// </summary>
    Task<CollectionServices> OpenAsync(StartRequest options, CancellationToken cancellationToken);
}
