using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Collection.Module;
#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.ClassicPages.Collection.Module;

/// <summary>
/// Replays committed acquisition receipts and checkpoints newly completed requests.
/// </summary>
internal sealed class AcquisitionJournal(CollectionContext context)
{
    private readonly ConcurrentDictionary<string, CollectedObservation> committed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

    internal ICollectionJournal Writer =>
        context.Journal ?? throw new InvalidOperationException("Classic Pages requires a collection journal.");

    internal IReadOnlyCollection<CollectedObservation> Records => committed.Values.ToArray();

    /// <summary>
    /// Checks whether a kind and request key already have a durable receipt.
    /// </summary>
    internal bool Contains(string kind, string key) => committed.ContainsKey(kind + ":" + key);

    /// <summary>
    /// Loads committed request identities without retaining every saved BLOB.
    /// </summary>
    internal async Task InitializeAsync(CancellationToken token)
    {
        foreach (CollectedObservation? record in await Writer.ReadCommittedAsync(token, metadataOnly: true))
            committed.TryAdd(record.SourceIdentity, record);
    }

    /// <summary>
    /// Serializes a request identity, replays its receipt, or commits the new response before returning it.
    /// </summary>
    internal async Task<T> ReadOrAcquireAsync<T>(
        string kind,
        string key,
        Func<Task<T>> acquire,
        CancellationToken token,
        Func<T, AcquisitionStatus>? status = null,
        Func<T, string?>? error = null)
    {
        string? identity = kind + ":" + key;
        SemaphoreSlim? gate = gates.GetOrAdd(identity, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            if (committed.TryGetValue(identity, out CollectedObservation? original))
                return CollectionJson.Read<T>((await Writer.ReadAsync(original.ObservationId, token)).RawBytes);
            T? value = await acquire();
            token.ThrowIfCancellationRequested();
            byte[]? bytes = CollectionJson.Bytes(value);
            CollectedObservation? record = new CollectedObservation(
                Id(identity),
                identity,
                null,
                status?.Invoke(value) ?? AcquisitionStatus.Complete,
                VersionedJson.From(new { kind, key }),
                bytes,
                error?.Invoke(value));
            await Writer.CommitAsync(
                new(record, VersionedJson.From(new { stage = "ClassicPageAcquisition", lastReceipt = identity })),
                token);
            committed.TryAdd(identity, record with { RawBytes = null });
            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Persists collection progress without fabricating a source observation.
    /// </summary>
    internal Task CheckpointAsync(object checkpoint, CancellationToken token) =>
        Writer.CommitAsync(new(null, VersionedJson.From(checkpoint)), token);

    /// <summary>
    /// Derives a stable observation ID from assessment and request identity.
    /// </summary>
    internal Guid Id(string key) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(context.AssessmentId.ToString("D") + "|" + key)).AsSpan(0, 16));

    /// <summary>
    /// Converts a source read outcome while preserving unrecognized outcomes as Unknown.
    /// </summary>
    internal static AcquisitionStatus Status(SourceReadState state) =>
        Enum.TryParse<AcquisitionStatus>(state.Status, out AcquisitionStatus status)
            ? status
            : AcquisitionStatus.Unknown;
}