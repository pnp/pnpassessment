#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts;

namespace PnP.Scanning.Core.Storage.Pipeline;

internal sealed record ManifestMember(Guid ObservationId, string SourceIdentity, string? SourceRevision,
    AcquisitionStatus AcquisitionStatus, string MetadataJson, string? AcquisitionError,
    Guid ArtifactId, long? Length, string? Sha256);

internal sealed record SnapshotManifest(int FormatVersion, Guid AssessmentId, Guid SnapshotId,
    string ModuleKey, string InputVersion, string ScopeJson, IReadOnlyList<ManifestMember> Members)
{
    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string Digest(string text) => Digest(Encoding.UTF8.GetBytes(text));

    internal static SnapshotManifest Build(SourceSnapshotRow snapshot,
        IEnumerable<SourceObservationRow> observations, IEnumerable<SourceArtifactRow> artifacts, bool artifactsVerified = false)
    {
        if (snapshot.FormatVersion != 1) throw new SnapshotIntegrityException("Unsupported snapshot format version.");
        ValidatePayload(snapshot.ScopeJson, "snapshot scope");
        var byId = artifacts.ToDictionary(x => x.ArtifactId);
        var members = new List<ManifestMember>();
        foreach (var observation in observations.OrderBy(x => x.ObservationId))
        {
            if (observation.SnapshotId != snapshot.SnapshotId || observation.ObservationId == Guid.Empty ||
                string.IsNullOrWhiteSpace(observation.SourceIdentity) || !Enum.IsDefined(observation.AcquisitionStatus))
                throw new SnapshotIntegrityException($"Invalid source identity for {observation.ObservationId}.");
            ValidatePayload(observation.MetadataJson, $"source metadata {observation.ObservationId}");
            if (!byId.Remove(observation.ArtifactId, out var artifact) ||
                artifact.ObservationId != observation.ObservationId || artifact.SnapshotId != snapshot.SnapshotId)
                throw new SnapshotIntegrityException($"Missing or incorrectly owned artifact for {observation.ObservationId}.");
            if (!artifactsVerified) ValidateArtifact(artifact, observation.AcquisitionStatus);
            members.Add(new(observation.ObservationId, observation.SourceIdentity, observation.SourceRevision,
                observation.AcquisitionStatus, observation.MetadataJson, observation.AcquisitionError,
                artifact.ArtifactId, artifact.Length, artifact.Sha256));
        }
        if (byId.Count != 0) throw new SnapshotIntegrityException("Snapshot contains unreferenced artifacts.");
        return new(snapshot.FormatVersion, snapshot.AssessmentId, snapshot.SnapshotId,
            snapshot.ModuleKey, snapshot.InputVersion, snapshot.ScopeJson, members);
    }

    internal static void ValidateArtifact(SourceArtifactRow artifact, AcquisitionStatus status)
    {
        if (artifact.RawBytes == null)
        {
            if (artifact.Length != null || artifact.Sha256 != null || status == AcquisitionStatus.Complete)
                throw new SnapshotIntegrityException($"NULL artifact has inconsistent acquisition, length or digest: {artifact.ArtifactId}.");
        }
        else if (artifact.Length != artifact.RawBytes.LongLength || artifact.Sha256 != Digest(artifact.RawBytes))
            throw new SnapshotIntegrityException($"Artifact length or SHA-256 mismatch: {artifact.ArtifactId}.");
    }

    private static void ValidatePayload(string json, string description)
    {
        try { _ = new VersionedJson(json); }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new SnapshotIntegrityException($"Invalid versioned {description}: {ex.Message}");
        }
    }

    internal string Serialize() => JsonSerializer.Serialize(this);
    internal string MemberIds() => JsonSerializer.Serialize(Members.Select(x => x.ObservationId));
    internal SnapshotInfo Info() => new(AssessmentId, SnapshotId, ModuleKey, InputVersion,
        new VersionedJson(ScopeJson), Digest(Serialize()), Members.Select(x => x.ObservationId));
}
