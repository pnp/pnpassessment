using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using PnP.Scanning.Core.Pipeline.Collection.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Pipeline.Collection.Module;

internal interface ICollectionModule
{
    string ModuleKey { get; }
    string InputVersion { get; }
    IAsyncEnumerable<CollectionRecord> CollectAsync(CollectionContext context, CancellationToken cancellationToken);
}

internal sealed record CollectionContext(
    StartRequest Options, VersionedJson Parameters, VersionedJson? Checkpoint,
    ICollectionEnvironment Environment, Guid AssessmentId = default, ICollectionJournal? Journal = null);

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
