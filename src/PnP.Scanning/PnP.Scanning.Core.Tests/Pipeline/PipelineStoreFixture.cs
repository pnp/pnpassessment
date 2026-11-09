#nullable enable
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;

namespace PnP.Scanning.Core.Tests.Pipeline;

internal sealed class PipelineStoreFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pnp-foundation", Guid.NewGuid().ToString("N"));
    internal Guid AssessmentId { get; } = Guid.NewGuid();
    internal Guid SnapshotId { get; } = Guid.NewGuid();
    internal Guid CollectionId { get; } = Guid.NewGuid();
    internal PipelineStore Store { get; }
    internal ISnapshotWriter Writer => Store.SnapshotWriter(AssessmentId, SnapshotId, CollectionId);
    internal ISnapshotReader Reader => Store.SnapshotReader(AssessmentId, SnapshotId);

    internal PipelineStoreFixture() => Store = new(Root);

    internal async Task InitializeAsync()
    {
        await Store.CreateCollectionAsync(
            new Scan { ScanId = AssessmentId, CLIMode = "fixture", Status = ScanStatus.Queued },
            new SourceSnapshotRow
            {
                AssessmentId = AssessmentId, SnapshotId = SnapshotId, ModuleKey = "fixture",
                InputVersion = "input-v1", ScopeJson = VersionedJson.From(new { scope = "synthetic" }).Json,
                CreatedAtUtc = DateTime.UtcNow,
            }, Phase(CollectionId, PhaseKind.Collection));
        await Store.SetRunningAsync(AssessmentId, CollectionId);
    }

    internal PhaseRunRow Phase(Guid id, PhaseKind kind) => new()
    {
        RunId = id, AssessmentId = AssessmentId, SnapshotId = SnapshotId, ModuleKey = "fixture",
        InputVersion = "input-v1", Kind = kind, CurrentPhase = kind, Status = ScanStatus.Queued,
        RuleVersion = kind == PhaseKind.Analysis ? "rules-v1" : null,
        ParametersJson = VersionedJson.From(new { option = "fixture" }).Json, CreatedAtUtc = DateTime.UtcNow,
    };

    internal static CollectedObservation Observation(AcquisitionStatus status = AcquisitionStatus.Complete, byte[]? bytes = null) =>
        new(Guid.NewGuid(), $"synthetic:{Guid.NewGuid():N}", "revision-1", status,
            VersionedJson.From(new { readState = "Unknown", value = (bool?)null }),
            status == AcquisitionStatus.Complete ? bytes ?? [0, 1, 255] : bytes,
            status == AcquisitionStatus.Complete ? null : "synthetic acquisition diagnostic");

    internal Task CommitAsync(CollectedObservation observation, int? expected = null) =>
        Writer.CommitAsync(new(observation, VersionedJson.From(new { last = observation.ObservationId }), expected), default);

    internal async Task<Guid> StartAnalysisAsync(string ruleVersion = "rules-v1")
    {
        var id = Guid.NewGuid();
        var phase = Phase(id, PhaseKind.Analysis);
        phase.RuleVersion = ruleVersion;
        await Store.CreateAnalysisAsync(phase);
        await Store.SetRunningAsync(AssessmentId, id);
        return id;
    }

    internal async Task MutateAsync(Func<ScanContext, Task> action)
    {
        using var db = Store.CreateContext(AssessmentId);
        await action(db);
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
