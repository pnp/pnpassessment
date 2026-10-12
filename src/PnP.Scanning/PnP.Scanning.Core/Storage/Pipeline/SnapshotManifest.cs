#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Storage.Pipeline;

/// <summary>
/// Fixes one source identity, acquisition metadata, content length and digest in the snapshot manifest.
/// </summary>
internal sealed record ManifestMember(Guid ObservationId, string SourceIdentity, string? SourceRevision,
    AcquisitionStatus AcquisitionStatus, string MetadataJson, string? AcquisitionError,
    long? Length, string? Sha256);

/// <summary>
/// Provides the deterministic snapshot envelope and member list used to seal and validate offline analysis inputs.
/// </summary>
internal sealed record SnapshotManifest(int FormatVersion, Guid AssessmentId, Guid SnapshotId,
    string ModuleKey, string InputVersion, string ScopeJson, IReadOnlyList<ManifestMember> Members)
{
    /// <summary>
    /// Computes a lowercase SHA-256 digest, encoding text as UTF-8 when required.
    /// </summary>
    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    /// <summary>
    /// Computes a lowercase SHA-256 digest, encoding text as UTF-8 when required.
    /// </summary>
    internal static string Digest(string text) => Digest(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Validates snapshot and observation identities, orders fixed members and constructs the manifest from saved metadata.
    /// </summary>
    internal static SnapshotManifest Build(SourceSnapshotRow snapshot,
        IEnumerable<SourceObservationRow> observations, bool contentVerified = false)
    {
        if (snapshot.FormatVersion != 1) throw new SnapshotIntegrityException("Unsupported snapshot format version.");
        ValidatePayload(snapshot.ScopeJson, "snapshot scope");
        var members = new List<ManifestMember>();
        foreach (var observation in observations.OrderBy(x => x.ObservationId))
        {
            if (observation.SnapshotId != snapshot.SnapshotId || observation.ObservationId == Guid.Empty ||
                string.IsNullOrWhiteSpace(observation.SourceIdentity) ||
                !Enum.IsDefined(observation.AcquisitionStatus))
                throw new SnapshotIntegrityException($"Invalid source identity for {observation.ObservationId}.");
            ValidatePayload(observation.MetadataJson, $"source metadata {observation.ObservationId}");
            if (!contentVerified) ValidateContent(observation);
            members.Add(new(observation.ObservationId, observation.SourceIdentity, observation.SourceRevision,
                observation.AcquisitionStatus, observation.MetadataJson, observation.AcquisitionError,
                observation.Length, observation.Sha256));
        }
        return new(snapshot.FormatVersion, snapshot.AssessmentId, snapshot.SnapshotId,
            snapshot.ModuleKey, snapshot.InputVersion, snapshot.ScopeJson, members);
    }

    /// <summary>
    /// Requires a consistent acquired BLOB length and digest, distinguishing missing content from an empty successful BLOB.
    /// </summary>
    internal static void ValidateContent(SourceObservationRow observation)
    {
        if (observation.RawBytes == null)
        {
            if (observation.Length != null || observation.Sha256 != null || observation.AcquisitionStatus == AcquisitionStatus.Complete)
                throw new SnapshotIntegrityException($"NULL source content has inconsistent acquisition, length or digest: {observation.ObservationId}.");
        }
        else if (observation.Length != observation.RawBytes.LongLength || observation.Sha256 != Digest(observation.RawBytes))
            throw new SnapshotIntegrityException($"Source content length or SHA-256 mismatch: {observation.ObservationId}.");
    }

    /// <summary>
    /// Requires a valid versioned JSON envelope and translates malformed metadata into a snapshot integrity failure.
    /// </summary>
    private static void ValidatePayload(string json, string description)
    {
        try { _ = new VersionedJson(json); }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new SnapshotIntegrityException($"Invalid versioned {description}: {ex.Message}");
        }
    }

    /// <summary>
    /// Serializes the deterministic manifest envelope and ordered members used for the pinned digest.
    /// </summary>
    internal string Serialize() => JsonSerializer.Serialize(this);
    /// <summary>
    /// Serializes the fixed ordered observation IDs stored alongside the sealed manifest.
    /// </summary>
    internal string MemberIds() => JsonSerializer.Serialize(Members.Select(x => x.ObservationId));
    /// <summary>
    /// Projects the validated manifest into read-only snapshot information for modules and lifecycle responses.
    /// </summary>
    internal SnapshotInfo Info() => new(AssessmentId, SnapshotId, ModuleKey, InputVersion,
        new VersionedJson(ScopeJson), Digest(Serialize()), Members.Select(x => x.ObservationId));
}
