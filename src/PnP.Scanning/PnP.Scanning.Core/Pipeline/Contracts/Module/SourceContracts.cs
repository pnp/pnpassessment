#nullable enable
using System.Collections.ObjectModel;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

internal enum AcquisitionStatus { Complete, Partial, Denied, NotAttempted, Failed, Unknown, NotReturned }
internal enum PhaseKind { Pipeline, Collection, Analysis }

/// <summary>Collector output: facts and original bytes, without an analysis projection.</summary>
internal sealed record CollectedObservation(
    Guid ObservationId, string SourceIdentity, string? SourceRevision,
    AcquisitionStatus AcquisitionStatus, VersionedJson Metadata, byte[]? RawBytes,
    string? AcquisitionError = null);

internal sealed record CollectionRecord(
    CollectedObservation? Observation, VersionedJson Checkpoint, int? ExpectedRecords = null);

/// <summary>Detached source data. Byte access returns a copy and never exposes a tracked entity.</summary>
internal sealed class SourceArtifact
{
    private readonly byte[]? bytes;

    internal SourceArtifact(long? length, string? sha256, byte[]? bytes)
    {
        Length = length;
        Sha256 = sha256;
        this.bytes = bytes?.ToArray();
    }

    public long? Length { get; }
    public string? Sha256 { get; }
    public byte[]? GetBytes() => bytes?.ToArray();
}

internal sealed record SourceRecord(
    Guid ObservationId, Guid SnapshotId, string SourceIdentity, string? SourceRevision,
    AcquisitionStatus AcquisitionStatus, VersionedJson Metadata, SourceArtifact Artifact,
    string? AcquisitionError);

internal sealed class SnapshotInfo
{
    internal SnapshotInfo(Guid assessmentId, Guid snapshotId, string moduleKey, string inputVersion,
        VersionedJson scope, string manifestDigest, IEnumerable<Guid> observationIds)
    {
        AssessmentId = assessmentId;
        SnapshotId = snapshotId;
        ModuleKey = moduleKey;
        InputVersion = inputVersion;
        Scope = scope;
        ManifestDigest = manifestDigest;
        ObservationIds = new ReadOnlyCollection<Guid>(observationIds.ToArray());
    }

    public Guid AssessmentId { get; }
    public Guid SnapshotId { get; }
    public string ModuleKey { get; }
    public string InputVersion { get; }
    public VersionedJson Scope { get; }
    public string ManifestDigest { get; }
    public IReadOnlyList<Guid> ObservationIds { get; }
}

internal interface ISnapshotWriter
{
    Task CommitAsync(CollectionRecord record, CancellationToken cancellationToken);
    Task<SnapshotInfo> SealAsync(CancellationToken cancellationToken);
}

internal interface ISnapshotReader
{
    Task<SnapshotInfo> OpenAsync(CancellationToken cancellationToken);
    Task<SourceRecord> ReadAsync(Guid observationId, CancellationToken cancellationToken);
}

internal sealed class SnapshotIntegrityException(string message) : IOException(message);
