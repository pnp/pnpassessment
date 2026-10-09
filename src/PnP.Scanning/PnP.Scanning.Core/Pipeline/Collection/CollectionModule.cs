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
    ICollectionEnvironment Environment);

internal interface ICollectionEnvironment
{
    StartRequest Protect(StartRequest options);
    StartRequest Restore(StartRequest options);
    Task<CollectionServices> OpenAsync(StartRequest options, CancellationToken cancellationToken);
}
