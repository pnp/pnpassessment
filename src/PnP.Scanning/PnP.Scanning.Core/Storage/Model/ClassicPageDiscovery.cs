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
    private readonly List<PageBaseTypeProjection> pageBaseTypeProjections = new();

    // Transient consumer boundary only. Durable raw-source mapping belongs to persistence/export.
    // These properties must not become an EF schema or native CSV change.
    [NotMapped, Ignore]
    public AspxFileObservation DiscoveryObservation { get; set; }

    [NotMapped, Ignore]
    public IReadOnlyList<AspxSourceReadResult> SourceReads => sourceReads.AsReadOnly();

    internal void RecordSourceRead(AspxSourceReadResult result) => sourceReads.Add(result);

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
}
