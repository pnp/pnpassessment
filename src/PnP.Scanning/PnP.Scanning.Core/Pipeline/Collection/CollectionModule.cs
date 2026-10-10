#nullable enable
using PnP.Scanning.Core.Pipeline.Contracts;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Pipeline.Collection;

internal interface ICollectionModule
{
    string ModuleKey { get; }
    string InputVersion { get; }
    IAsyncEnumerable<CollectionRecord> CollectAsync(CollectionContext context, CancellationToken cancellationToken);
}

/// <summary>Authentication and online services are available only within collection execution.</summary>
internal sealed record CollectionContext(
    StartRequest Options, VersionedJson Parameters, VersionedJson? Checkpoint,
    ICollectionEnvironment Environment, Guid AssessmentId = default, ICollectionJournal? Journal = null);

/// <summary>Bound to an unsealed collection. Allows durable acquisition receipts and checkpoint-only commits.</summary>
internal interface ICollectionJournal
{
    Task CommitAsync(CollectionRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<CollectedObservation>> ReadCommittedAsync(CancellationToken cancellationToken, bool metadataOnly = false);
    Task<CollectedObservation> ReadAsync(Guid observationId, CancellationToken cancellationToken);
}

internal interface ICollectionSnapshotValidator
{
    Task ValidateAsync(ICollectionJournal journal, CancellationToken cancellationToken);
}

internal interface ICollectionEnvironment
{
    StartRequest Protect(StartRequest options);
    StartRequest Restore(StartRequest options);
    Task<CollectionServices> OpenAsync(StartRequest options, CancellationToken cancellationToken);
}
