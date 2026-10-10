#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Storage.Pipeline;

internal sealed record ManifestMember(Guid ObservationId, string SourceIdentity, string? SourceRevision,
    AcquisitionStatus AcquisitionStatus, string MetadataJson, string? AcquisitionError,
    long? Length, string? Sha256);

internal sealed record SnapshotManifest(int FormatVersion, Guid AssessmentId, Guid SnapshotId,
    string ModuleKey, string InputVersion, string ScopeJson, IReadOnlyList<ManifestMember> Members)
{
    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string Digest(string text) => Digest(Encoding.UTF8.GetBytes(text));

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
