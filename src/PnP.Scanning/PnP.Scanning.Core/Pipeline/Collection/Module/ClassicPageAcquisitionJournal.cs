#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Collection.Module;

/// <summary>Every completed request is committed before its successor. Replays return the original receipt.</summary>
internal sealed class ClassicPageAcquisitionJournal(CollectionContext context)
{
    private readonly ConcurrentDictionary<string, CollectedObservation> committed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);
    internal ICollectionJournal Writer => context.Journal ?? throw new InvalidOperationException("Classic Pages requires a collection journal.");
    internal IReadOnlyCollection<CollectedObservation> Records => committed.Values.ToArray();
    internal bool Contains(string kind, string key) => committed.ContainsKey(kind + ":" + key);
    internal async Task InitializeAsync(CancellationToken token)
    {
        foreach (var record in await Writer.ReadCommittedAsync(token, metadataOnly: true)) committed.TryAdd(record.SourceIdentity, record);
    }
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
    internal Task CheckpointAsync(object checkpoint, CancellationToken token) => Writer.CommitAsync(new(null, VersionedJson.From(checkpoint)), token);
    internal Guid Id(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(context.AssessmentId.ToString("D") + "|" + key)).AsSpan(0, 16));
    internal static AcquisitionStatus Status(SourceReadState state) => Enum.TryParse<AcquisitionStatus>(state.Status, out var status) ? status : AcquisitionStatus.Unknown;
}
