using CsvHelper.Configuration.Attributes;
using System.ComponentModel.DataAnnotations.Schema;
using PnP.Scanning.Core.Discovery;

namespace PnP.Scanning.Core.Storage;

/// <summary>
/// One discovered physical ASPX or one acquisition-evidence row owned by the Classic assessment.
/// Scope rows never represent invented pages. Page existence survives enrichment failures.
/// </summary>
internal sealed class ClassicPageDiscovery : BaseScanResult
{
    private readonly List<AspxSourceReadResult> sourceReads = new();
    private readonly List<string> sourceReadObservationIds = new();
    private readonly List<PageBaseTypeProjection> pageBaseTypeProjections = new();

    // Keep the reusable acquisition contracts transient. The writer maps them to the versioned
    // SourceEvidenceJson document, without making the parser/reader an EF entity or CSV serializer.
    [NotMapped, Ignore]
    public AspxFileObservation DiscoveryObservation { get; set; }

    [NotMapped, Ignore]
    public IReadOnlyList<AspxSourceReadResult> SourceReads => sourceReads.AsReadOnly();

    internal void RecordSourceRead(AspxSourceReadResult result)
    {
        sourceReads.Add(result);
        sourceReadObservationIds.Add(Guid.NewGuid().ToString("N"));
    }

    internal IReadOnlyList<string> SourceReadObservationIds => sourceReadObservationIds;

    [NotMapped, Ignore]
    public IReadOnlyList<PageBaseTypeProjection> PageBaseTypeProjections => pageBaseTypeProjections.AsReadOnly();

    internal void RecordBaseTypeProjection(PageBaseTypeProjection result) => pageBaseTypeProjections.Add(result);

    public string RecordKey { get; set; }
    public string RowType { get; set; }
    public string ScopeType { get; set; }
    public string ParentScopeKey { get; set; }
    public string Url { get; set; }
    public Guid? SiteCollectionId { get; set; }
    public Guid? WebId { get; set; }
    public Guid? ListId { get; set; }
    public Guid? FolderUniqueId { get; set; }
    public Guid? FileUniqueId { get; set; }
    public int? ListItemId { get; set; }
    public string FileName { get; set; }

    public string AssetPurpose { get; set; } = "Unknown";
    public string AssetPurposeStatus { get; set; } = "Unknown";
    public string AssetPurposeReason { get; set; } = "NotEvaluated";

    [Ignore]
    public string PageType { get; set; }

    public string ContentTypeId { get; set; }

    public string DeclaredPageType { get; set; }
    public string ResolvedPageType { get; set; }
    public string PageTypeEvidenceOrigin { get; set; } = "None";
    public string PageTypeSourceStatus { get; set; } = "Unknown";
    public string PageTypeResolutionStatus { get; set; } = "Unknown";
    public string PublishingLayoutFamily { get; set; } = "Unknown";
    public string PageTypeReason { get; set; } = "NotEvaluated";
    public string PageTypeEvidenceJson { get; set; }

    public bool? HomePage { get; set; }
    public bool? LibraryHidden { get; set; }
    public string ObservationMethod { get; set; }
    public string DiscoveryStatus { get; set; }
    public string AssessmentStatus { get; set; }
    public int? ExpectedChildCount { get; set; }
    public int? ObservedChildCount { get; set; }
    public string ErrorStage { get; set; }
    public string ErrorCodes { get; set; }
    public string ErrorDetail { get; set; }
    public string EvidenceJson { get; set; }
    public DateTime ObservedAtUtc { get; set; }

    private string sourceEvidenceJson;
    private ClassicSourceEvidence.Snapshot sourceEvidence;
    /// <summary>Versioned original discovery and per-read evidence, including base64 original bytes.</summary>
    [NullValues("")]
    public string SourceEvidenceJson
    {
        get => sourceEvidenceJson;
        set { sourceEvidenceJson = value; sourceEvidence = null; }
    }

    internal ClassicSourceEvidence.Snapshot ReadSourceEvidence() =>
        sourceEvidence ??= ClassicSourceEvidence.Read(SourceEvidenceJson);
    private ClassicSourceEvidence.ReadObservation LatestRead => ReadSourceEvidence().Reads.LastOrDefault();
    private ClassicSourceEvidence.Projection LatestProjection => LatestRead?.Page;
    private AspxFileObservation OriginalDiscovery => ReadSourceEvidence().DiscoveryObservations.FirstOrDefault();

    // Additive CSV conveniences select one complete latest observation, never a synthetic blend of
    // success and failure facets. SourceEvidenceJson retains all previous observations and bytes.
    [NotMapped] public string SourceEvidenceState => LatestRead != null ? "Collected" : SourceEvidenceJson != null ? "DiscoveryOnly" :
        RowType == "Page" ? "NotCollected" : "NotApplicable";
    [NotMapped] public string OriginalDiscoveryUrl => OriginalDiscovery?.Identity.Url;
    [NotMapped] public string OriginalDiscoveryFileName => OriginalDiscovery?.Identity.Name;
    [NotMapped] public string OriginalDiscoveryObservedAtUtc => OriginalDiscovery == null ? null : ClassicSourceEvidence.Utc(OriginalDiscovery.ObservedAtUtc);
    [NotMapped] public string SourceObservationId => LatestRead?.ObservationId;
    [NotMapped] public Guid? SourceSiteCollectionId => LatestRead?.PhysicalIdentity.SiteCollectionId;
    [NotMapped] public Guid? SourceWebId => LatestRead?.PhysicalIdentity.WebId;
    [NotMapped] public Guid? SourceFileUniqueId => LatestRead?.PhysicalIdentity.FileUniqueId;
    [NotMapped] public Guid? SourceListId => LatestRead?.PhysicalIdentity.ListId;
    [NotMapped] public int? SourceListItemId => LatestRead?.PhysicalIdentity.ListItemId;
    [NotMapped] public string SourceUrl => LatestRead?.PhysicalIdentity.Url;
    [NotMapped] public string SourceFileName => LatestRead?.PhysicalIdentity.Name;
    [NotMapped] public string SourceIdentityState => LatestRead?.PhysicalIdentity.State;
    [NotMapped] public string SourceIdentityReason => LatestRead?.PhysicalIdentity.Reason;
    [NotMapped] public string SourceVersionState => LatestRead?.VersionState;
    [NotMapped] public string SourceVersionReason => LatestRead?.VersionReason;
    [NotMapped] public string SourceETag => LatestRead?.ETag;
    [NotMapped] public int? SourceMajorVersion => LatestRead?.MajorVersion;
    [NotMapped] public int? SourceMinorVersion => LatestRead?.MinorVersion;
    [NotMapped] public string SourceObservedAtUtc => LatestRead?.SourceObservedAtUtc;
    [NotMapped] public string SourceArtifactReference => LatestRead?.ArtifactReference;
    [NotMapped] public long? SourceCapturedByteLength => LatestRead?.CapturedByteLength;
    [NotMapped] public long? SourceExpectedByteLength => LatestRead?.ExpectedByteLength;
    [NotMapped] public string SourceRawDigestAlgorithm => LatestRead?.RawDigestAlgorithm;
    [NotMapped] public string SourceRawDigest => LatestRead?.RawDigest;
    [NotMapped] public string SourceRawDigestScope => LatestRead?.RawDigestScope;
    [NotMapped] public string SourceEncoding => LatestRead?.EncodingName;
    [NotMapped] public string SourceReadState => LatestRead?.ReadState;
    [NotMapped] public string SourceReadReason => LatestRead?.ReadReason;
    [NotMapped] public string SourceCaptureState => LatestRead?.CaptureState;
    [NotMapped] public string SourceDecodingState => LatestRead?.DecodingState;
    [NotMapped] public string SourceDecodingReason => LatestRead?.DecodingReason;
    [NotMapped] public string SourceContentState => LatestRead?.ContentState;
    [NotMapped] public string SourceContentReason => LatestRead?.ContentReason;
    [NotMapped] public string DeclaredInherits => LatestProjection?.DeclaredInherits;
    [NotMapped] public string NormalizedInherits => LatestProjection?.NormalizedInherits;
    [NotMapped] public string BaseType => LatestProjection?.BaseType;
    [NotMapped] public string TypeSource => LatestProjection?.TypeSource;
    [NotMapped] public string BaseTypeReason => LatestProjection?.Reason;
    [NotMapped] public bool? FrameworkDefaultAssumption => LatestProjection?.FrameworkDefaultAssumption;
    [NotMapped] public string PageParseState => LatestProjection?.ParseState;
    [NotMapped] public string PageParseReason => LatestProjection?.ParseReason;
    [NotMapped] public bool? PageParseReliable => LatestProjection?.ReliableParse;
    [NotMapped] public bool? VerifiedInheritsAbsence => LatestProjection?.VerifiedAbsence;
    [NotMapped] public string ConfigurationKnowledgeState => LatestProjection?.ConfigurationKnowledgeState;
    [NotMapped] public string ConfigurationApplicability => LatestProjection?.ConfigurationApplicability;
    [NotMapped] public string EffectivePagesPageBaseType => LatestProjection?.EffectivePagesPageBaseType;
    [NotMapped] public string PageBaseTypeProvenance => LatestProjection?.ConfigurationProvenance;
    [NotMapped] public string ConfigurationReason => LatestProjection?.ConfigurationReason;
    [NotMapped] public string ConfigurationEvidenceJson => ClassicSourceEvidence.ConfigurationJson(LatestProjection);
    [NotMapped] public string ObservedHandlerState => LatestRead?.ObservedHandlerState;
    [NotMapped] public string ObservedHandlerReason => LatestRead?.ObservedHandlerReason;
}
