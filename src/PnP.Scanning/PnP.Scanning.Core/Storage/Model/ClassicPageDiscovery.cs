using CsvHelper.Configuration.Attributes;

namespace PnP.Scanning.Core.Storage;

/// <summary>
/// One discovered physical ASPX or one acquisition-evidence row owned by the Classic assessment.
/// Scope rows never represent invented pages. Page existence survives enrichment failures.
/// </summary>
internal sealed class ClassicPageDiscovery : BaseScanResult
{
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

    [Ignore]
    public string PageType { get; set; }

    [Ignore]
    public string ContentTypeId { get; set; }

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
