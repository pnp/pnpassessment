#nullable enable
using System.Collections.ObjectModel;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Pipeline.Contracts.Module;

/// <summary>
/// Defines how completely a source was acquired, including partial, denied, unattempted, failed and unknown reads.
/// </summary>
internal enum AcquisitionStatus { Complete, Partial, Denied, NotAttempted, Failed, Unknown, NotReturned }
/// <summary>
/// Identifies a combined pipeline run or its collection and analysis phases.
/// </summary>
internal enum PhaseKind { Pipeline, Collection, Analysis }

/// <summary>
/// Carries source identity, revision, acquisition status, metadata and original bytes emitted by a collector.
/// </summary>
internal sealed record CollectedObservation(
    Guid ObservationId, string SourceIdentity, string? SourceRevision,
    AcquisitionStatus AcquisitionStatus, VersionedJson Metadata, byte[]? RawBytes,
    string? AcquisitionError = null);

/// <summary>
/// Combines an optional observation with its durable collector checkpoint and expected record count.
/// </summary>
internal sealed record CollectionRecord(
    CollectedObservation? Observation, VersionedJson Checkpoint, int? ExpectedRecords = null);

/// <summary>
/// Provides detached content bytes, saved length and digest with defensive copies for offline consumers.
/// </summary>
internal sealed class SourceArtifact
{
    private readonly byte[]? bytes;

    /// <summary>
    /// Copies the saved content and binds its length and digest without retaining a mutable storage entity.
    /// </summary>
    internal SourceArtifact(long? length, string? sha256, byte[]? bytes)
    {
        Length = length;
        Sha256 = sha256;
        this.bytes = bytes?.ToArray();
    }

    public long? Length { get; }
    public string? Sha256 { get; }
    /// <summary>
    /// Returns a copy of the acquired bytes, or null when content was not acquired.
    /// </summary>
    public byte[]? GetBytes() => bytes?.ToArray();
}

/// <summary>
/// Combines a sealed observation identity and metadata with its detached content and acquisition diagnostics.
/// </summary>
internal sealed record SourceRecord(
    Guid ObservationId, Guid SnapshotId, string SourceIdentity, string? SourceRevision,
    AcquisitionStatus AcquisitionStatus, VersionedJson Metadata, SourceArtifact Artifact,
    string? AcquisitionError);

/// <summary>
/// Describes the fixed scope, input version, manifest digest and member IDs of a selected sealed snapshot.
/// </summary>
internal sealed class SnapshotInfo
{
    /// <summary>
    /// Binds snapshot metadata and copies the ordered member IDs into a read-only collection.
    /// </summary>
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

/// <summary>
/// Allows collection to atomically commit source records and publish a sealed snapshot.
/// </summary>
internal interface ISnapshotWriter
{
    /// <summary>
    /// Commits source facts, content and recovery progress in one transaction.
    /// </summary>
    Task CommitAsync(CollectionRecord record, CancellationToken cancellationToken);
    /// <summary>
    /// Validates saved content and fixes the snapshot manifest before making it available to analysis.
    /// </summary>
    Task<SnapshotInfo> SealAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Exposes validated, read-only access to one explicitly selected sealed snapshot.
/// </summary>
internal interface ISnapshotReader
{
    /// <summary>
    /// Validates and pins the selected snapshot manifest and fixed member list.
    /// </summary>
    Task<SnapshotInfo> OpenAsync(CancellationToken cancellationToken);
    /// <summary>
    /// Reads one pinned snapshot member as detached metadata and content after integrity validation.
    /// </summary>
    Task<SourceRecord> ReadAsync(Guid observationId, CancellationToken cancellationToken);
}

/// <summary>
/// Reports an inconsistent or corrupted snapshot so collection, analysis or report export cannot claim success.
/// </summary>
internal sealed class SnapshotIntegrityException(string message) : IOException(message);
