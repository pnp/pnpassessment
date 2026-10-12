#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Collection.Module;

/// <summary>
/// Persists acquisition receipts and reconstructs completed request identities so a resumed collection reuses the original responses.
/// </summary>
internal sealed class ClassicPageAcquisitionJournal(CollectionContext context)
{
    private readonly ConcurrentDictionary<string, CollectedObservation> committed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    internal ICollectionJournal Writer => context.Journal ?? throw new InvalidOperationException("Classic Pages requires a collection journal.");
    internal IReadOnlyCollection<CollectedObservation> Records => committed.Values.ToArray();
    /// <summary>
    /// Checks whether a request kind and key already have a committed acquisition receipt.
    /// </summary>
    internal bool Contains(string kind, string key) => committed.ContainsKey(kind + ":" + key);
    /// <summary>
    /// Loads committed receipt identities without BLOBs before replaying or continuing acquisition.
    /// </summary>
    internal async Task InitializeAsync(CancellationToken token)
    {
        foreach (var record in await Writer.ReadCommittedAsync(token, metadataOnly: true)) committed.TryAdd(record.SourceIdentity, record);
    }
    /// <summary>
    /// Serializes acquisition by request identity, replays an existing saved payload, or commits the new payload before returning it.
    /// </summary>
    internal async Task<T> ReadOrAcquireAsync<T>(string kind, string key, Func<Task<T>> acquire, CancellationToken token,
        Func<T, AcquisitionStatus>? status = null, Func<T, string?>? error = null)
    {
        var identity = kind + ":" + key;
        var gate = gates.GetOrAdd(identity, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            if (committed.TryGetValue(identity, out var original))
                return ClassicPageSourceJson.Read<T>((await Writer.ReadAsync(original.ObservationId, token)).RawBytes);
            var value = await acquire();
            token.ThrowIfCancellationRequested();
            var bytes = ClassicPageSourceJson.Bytes(value);
            var record = new CollectedObservation(Id(identity), identity, null, status?.Invoke(value) ?? AcquisitionStatus.Complete,
                VersionedJson.From(new { kind, key }), bytes, error?.Invoke(value));
            await Writer.CommitAsync(new(record, VersionedJson.From(new { stage = "ClassicPageAcquisition", lastReceipt = identity })), token);
            committed.TryAdd(identity, record with { RawBytes = null });
            return value;
        }
        finally { gate.Release(); }
    }
    /// <summary>
    /// Persists collector progress without adding a source observation.
    /// </summary>
    internal Task CheckpointAsync(object checkpoint, CancellationToken token) => Writer.CommitAsync(new(null, VersionedJson.From(checkpoint)), token);
    /// <summary>
    /// Derives a stable observation ID from the assessment and request identity for idempotent collection replay.
    /// </summary>
    internal Guid Id(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(context.AssessmentId.ToString("D") + "|" + key)).AsSpan(0, 16));
    /// <summary>
    /// Converts a recorded read state into a persisted acquisition status, retaining unknown states explicitly.
    /// </summary>
    internal static AcquisitionStatus Status(SourceReadState state) => Enum.TryParse<AcquisitionStatus>(state.Status, out var status) ? status : AcquisitionStatus.Unknown;
}
