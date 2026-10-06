using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Discovery;

internal enum AspxIdentityState { Resolved, Unresolved }
internal enum AspxSourceTransportState { NotAttempted, NotReturned, Complete, Denied, Failed, Partial }
internal enum AspxSourceCaptureState { NotCaptured, Complete, Partial }
internal enum AspxSourceDecodingState { NotAttempted, Reliable, Unreliable }
internal enum AspxSourceContentState { NotInspected, Source, Empty, LoginShell, SemanticDenied, RenderedHtml, Unknown }

/// <summary>A physical identity is the Site/Web/File tuple, never its mutable URL or item number.</summary>
internal sealed record AspxFileIdentity(Guid? SiteCollectionId, Guid? WebId, Guid? FileUniqueId,
    string Url, string Name, Guid? ListId = null, int? ListItemId = null)
{
    internal AspxIdentityState State => Present(SiteCollectionId) && Present(WebId) && Present(FileUniqueId)
        ? AspxIdentityState.Resolved : AspxIdentityState.Unresolved;
    internal string Reason => State == AspxIdentityState.Resolved ? "SiteWebFileIdsObserved" :
        string.Join(';', new[]
        {
            Present(SiteCollectionId) ? null : "SiteCollectionIdNotReturned",
            Present(WebId) ? null : "WebIdNotReturned",
            Present(FileUniqueId) ? null : "FileUniqueIdNotReturned",
        }.Where(value => value != null));
    internal string ListIdentityState => Present(ListId) ? "Observed" : "Unavailable";
    internal string ListIdentityReason => Present(ListId) ? "ListIdObserved" : "ListIdNotReturned";
    internal string ItemIdentityState => ListItemId > 0 ? "Observed" : "Unavailable";
    internal string ItemIdentityReason => ListItemId > 0 ? "ListItemIdRetainedFromDiscovery" : "ListItemNotReturnedOrNotApplicable";

    internal bool SamePhysicalFile(AspxFileIdentity other) => State == AspxIdentityState.Resolved &&
        other?.State == AspxIdentityState.Resolved && SiteCollectionId == other.SiteCollectionId &&
        WebId == other.WebId && FileUniqueId == other.FileUniqueId;

    internal static bool Present(Guid? value) => value.HasValue && value.Value != Guid.Empty;
}

/// <summary>An immutable discovery snapshot, including the unnormalized original record.</summary>
internal sealed record AspxFileObservation(Guid ScanId, string RecordKey, string SiteUrl, string WebUrl,
    AspxFileIdentity Identity, DateTimeOffset ObservedAtUtc, RawDiscoveryRecord DiscoveryRecord)
{
    internal static AspxFileObservation FromDiscovery(ClassicPageDiscovery row, RawDiscoveryRecord record = null)
    {
        // Copy caller-owned metadata so later enrichment cannot rewrite discovery evidence.
        if (record?.Metadata != null)
            record = record with { Metadata = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(record.Metadata, StringComparer.Ordinal)) };
        var observed = row.ObservedAtUtc == default ? DateTimeOffset.UtcNow :
            new DateTimeOffset(DateTime.SpecifyKind(row.ObservedAtUtc, DateTimeKind.Utc));
        return new(row.ScanId, row.RecordKey, row.SiteUrl, row.WebUrl,
            new(row.SiteCollectionId, row.WebId, row.FileUniqueId, row.Url, row.FileName, row.ListId, row.ListItemId),
            observed, record);
    }
}

/// <summary>File metadata observed for this read, not an assertion of an atomic server snapshot.</summary>
internal sealed record AspxSourceVersion(DateTimeOffset ObservedAtUtc, string ETag = null,
    int? MajorVersion = null, int? MinorVersion = null)
{
    internal string State => !string.IsNullOrWhiteSpace(ETag) || MajorVersion.HasValue && MinorVersion.HasValue
        ? "ObservedVersion" : "ObservationTimeOnly";
    internal string Reason => State == "ObservedVersion" ? "FileVersionMetadataObservedBeforeDownload" :
        "VersionMetadataNotReturned;SourceObservationUtcRetained";
}

internal sealed record AspxSourceDecoding(AspxSourceDecodingState State, string EncodingName,
    int BomByteCount, string Reason);

/// <summary>
/// Reusable in-memory acquisition evidence. Persistence/export is a separate consumer boundary.
/// A null capture is not a zero-byte response; a prefix digest is not a whole-file digest.
/// </summary>
internal sealed class AspxSourceReadResult
{
    private readonly byte[] originalBytes;

    internal AspxSourceReadResult(AspxFileObservation discovery, AspxFileIdentity physicalIdentity,
        AspxSourceVersion version, AspxSourceTransportState transportState, string transportReason,
        AspxSourceCaptureState captureState, byte[] bytes, long? expectedByteLength,
        AspxSourceDecoding decoding, string decodedText, AspxSourceContentState contentState,
        string contentReason, int? httpStatusCode = null)
    {
        Discovery = discovery;
        PhysicalIdentity = physicalIdentity;
        Version = version with { ObservedAtUtc = version.ObservedAtUtc.ToUniversalTime() };
        TransportState = transportState;
        TransportReason = transportReason;
        CaptureState = captureState;
        originalBytes = bytes?.ToArray();
        ExpectedByteLength = expectedByteLength;
        Decoding = decoding;
        DecodedText = decodedText;
        ContentState = contentState;
        ContentReason = contentReason;
        HttpStatusCode = httpStatusCode;
        RawDigest = originalBytes == null ? null : Convert.ToHexString(SHA256.HashData(originalBytes));
        LegacySourceHash = decodedText == null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(decodedText)));
    }

    internal AspxFileObservation Discovery { get; }
    internal AspxFileIdentity PhysicalIdentity { get; }
    internal AspxSourceVersion Version { get; }
    internal AspxSourceTransportState TransportState { get; }
    internal string TransportReason { get; }
    internal int? HttpStatusCode { get; }
    internal AspxSourceCaptureState CaptureState { get; }
    // Returning a copy prevents downstream mutation from invalidating retained evidence.
    internal byte[] OriginalBytes => originalBytes?.ToArray();
    internal long? CapturedByteLength => originalBytes?.LongLength;
    internal long? ExpectedByteLength { get; }
    internal string ExpectedLengthState => ExpectedByteLength.HasValue ? "Observed" : "NotReturned";
    internal string ExpectedLengthReason => ExpectedByteLength.HasValue ? "ExpectedFileByteLengthObserved" : "ExpectedFileByteLengthNotReturned";
    internal string RawDigestAlgorithm => originalBytes == null ? null : "SHA256";
    internal string RawDigest { get; }
    internal string RawDigestScope => CaptureState switch
    {
        AspxSourceCaptureState.Complete => "CompleteCapturedResponse",
        AspxSourceCaptureState.Partial => "PartialCapturedBytes",
        _ => "NotCaptured",
    };
    internal AspxSourceDecoding Decoding { get; }
    internal string DecodedText { get; }
    internal AspxSourceContentState ContentState { get; }
    internal string ContentReason { get; }
    internal string LegacySourceHash { get; }
    internal const string LegacySourceHashKind = "DecodedTextUtf8Sha256";
    internal string IdentityComparisonReason => string.Join(';', new[]
    {
        AspxFileIdentity.Present(Discovery.Identity.SiteCollectionId) && AspxFileIdentity.Present(PhysicalIdentity.SiteCollectionId) &&
            Discovery.Identity.SiteCollectionId != PhysicalIdentity.SiteCollectionId
            ? "DiscoverySiteIdentityChanged" : null,
        AspxFileIdentity.Present(Discovery.Identity.WebId) && AspxFileIdentity.Present(PhysicalIdentity.WebId) &&
            Discovery.Identity.WebId != PhysicalIdentity.WebId
            ? "DiscoveryWebIdentityChanged" : null,
        AspxFileIdentity.Present(Discovery.Identity.FileUniqueId) && AspxFileIdentity.Present(PhysicalIdentity.FileUniqueId) &&
            Discovery.Identity.FileUniqueId != PhysicalIdentity.FileUniqueId
            ? "DiscoveryFileIdentityChanged" : null,
    }.Where(reason => reason != null));
    internal string IdentityComparisonState => IdentityComparisonReason.Length == 0 ? "NoKnownIdentityConflict" : "Changed";
    internal bool IsReliableSource => TransportState == AspxSourceTransportState.Complete &&
        CaptureState == AspxSourceCaptureState.Complete && CapturedByteLength > 0 &&
        Decoding.State == AspxSourceDecodingState.Reliable && ContentState == AspxSourceContentState.Source &&
        PhysicalIdentity.State == AspxIdentityState.Resolved && IdentityComparisonState != "Changed";

    internal static AspxSourceReadResult Unavailable(AspxFileObservation discovery,
        AspxSourceTransportState state, string reason, AspxFileIdentity identity = null,
        AspxSourceVersion version = null, long? expectedByteLength = null, int? httpStatusCode = null) =>
        new(discovery, identity ?? discovery.Identity, version ?? new(DateTimeOffset.UtcNow), state, reason,
            AspxSourceCaptureState.NotCaptured, null, expectedByteLength,
            new(AspxSourceDecodingState.NotAttempted, null, 0, "NoBytesCaptured"), null,
            AspxSourceContentState.NotInspected, "NoBytesCaptured", httpStatusCode);
}
