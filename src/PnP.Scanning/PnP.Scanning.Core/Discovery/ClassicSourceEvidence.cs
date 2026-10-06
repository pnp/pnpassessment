using PnP.Scanning.Core.Storage;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// Minimal assessment-owned source storage: one versioned document on the existing physical
/// inventory row. Bytes live in the database, not a remote URL, sidecar or separate artifact catalog.
/// Each read retains its own identity, discovery snapshot, projection and independent outcomes.
/// </summary>
internal static class ClassicSourceEvidence
{
    internal const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    internal sealed record Identity(Guid? SiteCollectionId, Guid? WebId, Guid? FileUniqueId,
        string Url, string Name, Guid? ListId, int? ListItemId, string State, string Reason,
        string ListState, string ListReason, string ItemState, string ItemReason)
    {
        internal static Identity Capture(AspxFileIdentity file) => new(file.SiteCollectionId, file.WebId,
            file.FileUniqueId, file.Url, file.Name, file.ListId, file.ListItemId, file.State.ToString(), file.Reason,
            file.ListIdentityState, file.ListIdentityReason, file.ItemIdentityState, file.ItemIdentityReason);
    }

    internal sealed record Projection(string DeclaredInherits, string NormalizedInherits, string BaseType,
        string TypeSource, string Reason, bool FrameworkDefaultAssumption, string ParseState,
        string ParseReason, bool ReliableParse, bool VerifiedAbsence, PageDirectiveEvidence[] Directives,
        string ConfigurationKnowledgeState, string ConfigurationApplicability, string EffectivePagesPageBaseType,
        string ConfigurationProvenance, string ConfigurationReason, PageBaseTypeConfigurationEvidence[] ConfigurationEvidence)
    {
        internal static Projection Capture(PageBaseTypeProjection value) => value == null ? null : new(
            value.DeclaredInherits, value.NormalizedInherits, value.BaseType, value.TypeSource.ToString(), value.Reason,
            value.IsFrameworkDefaultAssumption, value.Parse.Status.ToString(), value.Parse.Reason, value.IsReliableParse,
            value.IsVerifiedAbsence, value.Parse.Directives.ToArray(), value.Configuration.KnowledgeState.ToString(),
            value.Configuration.Applicability.ToString(), value.Configuration.EffectivePageBaseType,
            value.Configuration.Provenance, value.Configuration.Reason, value.Configuration.Evidence.ToArray());
    }

    internal sealed record ReadObservation(string ObservationId, AspxFileObservation Discovery, Identity PhysicalIdentity,
        string SourceObservedAtUtc, string VersionState, string VersionReason, string ETag, int? MajorVersion,
        int? MinorVersion, string IdentityComparisonState, string IdentityComparisonReason,
        string ReadState, string ReadReason, int? HttpStatusCode, string CaptureState, byte[] OriginalBytes,
        long? CapturedByteLength, long? ExpectedByteLength, string ExpectedLengthState, string ExpectedLengthReason,
        string RawDigestAlgorithm, string RawDigest, string RawDigestScope, string EncodingName, int BomByteCount,
        string DecodingState, string DecodingReason, string ContentState, string ContentReason,
        bool ReliableSource, string LegacySourceHash, string LegacySourceHashKind, string ArtifactReference,
        Projection Page, string ObservedHandlerState, string ObservedHandlerReason)
    {
        internal static ReadObservation Capture(string id, AspxSourceReadResult read, PageBaseTypeProjection projection) => new(
            id, read.Discovery, Identity.Capture(read.PhysicalIdentity), Utc(read.Version.ObservedAtUtc),
            read.Version.State, read.Version.Reason, read.Version.ETag, read.Version.MajorVersion, read.Version.MinorVersion,
            read.IdentityComparisonState, read.IdentityComparisonReason, read.TransportState.ToString(), read.TransportReason,
            read.HttpStatusCode, read.CaptureState.ToString(), read.OriginalBytes, read.CapturedByteLength,
            read.ExpectedByteLength, read.ExpectedLengthState, read.ExpectedLengthReason, read.RawDigestAlgorithm,
            read.RawDigest, read.RawDigestScope, read.Decoding.EncodingName, read.Decoding.BomByteCount,
            read.Decoding.State.ToString(), read.Decoding.Reason, read.ContentState.ToString(), read.ContentReason,
            read.IsReliableSource, read.LegacySourceHash, AspxSourceReadResult.LegacySourceHashKind,
            read.CapturedByteLength.HasValue ? ClassicSourceEvidence.ArtifactReference(read.Discovery.ScanId, read.Discovery.RecordKey, id) : null,
            Projection.Capture(projection), "ServerOnlyUnavailable",
            "ServerDiagnosticsNotCollected;PhysicalSourceIsNotAnObservedRequestHandler");

        internal byte[] RetrieveBytes()
        {
            if (OriginalBytes == null) return null;
            if (CapturedByteLength != OriginalBytes.LongLength || RawDigestAlgorithm != "SHA256" ||
                !string.Equals(RawDigest, Convert.ToHexString(SHA256.HashData(OriginalBytes)), StringComparison.Ordinal))
                throw new InvalidDataException("Recorded source artifact length or raw-byte digest does not match its bytes.");
            return OriginalBytes.ToArray();
        }
    }

    internal sealed record Snapshot(int Version, AspxFileObservation[] DiscoveryObservations, ReadObservation[] Reads);

    internal static Snapshot Read(string json)
    {
        if (json == null) return new(CurrentVersion, Array.Empty<AspxFileObservation>(), Array.Empty<ReadObservation>());
        try
        {
            var result = JsonSerializer.Deserialize<Snapshot>(json, Options);
            if (result?.Version == CurrentVersion && result.DiscoveryObservations != null && result.Reads != null &&
                result.DiscoveryObservations.All(value => value != null) &&
                result.Reads.All(value => value != null && !string.IsNullOrEmpty(value.ObservationId)))
                return result;
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid persisted source evidence; refusing to discard it.", ex); }
        throw new InvalidDataException("Unsupported persisted source evidence; refusing to replace it with unobserved data.");
    }

    internal static void Merge(ClassicPageDiscovery previous, ClassicPageDiscovery current, bool collect)
    {
        // Metadata-only writes and replay of already committed rows cannot erase or duplicate history.
        var prior = Read(previous?.SourceEvidenceJson);
        var incoming = Read(current.SourceEvidenceJson);
        var discoveries = prior.DiscoveryObservations.Concat(incoming.DiscoveryObservations);
        var reads = prior.Reads.Concat(incoming.Reads);
        if (collect)
        {
            if (current.DiscoveryObservation != null) discoveries = discoveries.Append(current.DiscoveryObservation);
            reads = reads.Concat(current.SourceReads.Select((read, index) => ReadObservation.Capture(
                current.SourceReadObservationIds[index], read,
                current.PageBaseTypeProjections.LastOrDefault(value => ReferenceEquals(value.SourceRead, read)))));
        }
        var retainedDiscoveries = discoveries.GroupBy(value => JsonSerializer.Serialize(value, Options), StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        var retainedReads = reads.GroupBy(value => value.ObservationId, StringComparer.Ordinal).Select(group =>
        {
            var variants = group.Select(value => JsonSerializer.Serialize(value, Options)).Distinct(StringComparer.Ordinal).ToArray();
            if (variants.Length != 1) throw new InvalidDataException("Conflicting source evidence for the same observation ID.");
            return group.First();
        }).ToArray();
        current.SourceEvidenceJson = retainedDiscoveries.Length == 0 && retainedReads.Length == 0 ? null :
            JsonSerializer.Serialize(new Snapshot(CurrentVersion, retainedDiscoveries, retainedReads), Options);
    }

    internal static string ConfigurationJson(Projection projection) => projection == null ? null :
        JsonSerializer.Serialize(projection.ConfigurationEvidence, Options);

    internal static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    internal static string ArtifactReference(Guid scanId, string recordKey, string observationId) =>
        $"assessment.db#cp1/{scanId:D}/{Uri.EscapeDataString(recordKey)}/{observationId}";
}
