#nullable enable
namespace PnP.Scanning.Core.Pipeline.Contracts.Page;

/// <summary>
/// Retains native physical-file observations before admission or offline classification.
/// </summary>
internal sealed class PhysicalFileFacts
{
    public Guid SiteCollectionId { get; set; }
    public Guid FileUniqueId { get; set; }
    public Guid ScanId { get; set; }
    public Guid? WebId { get; set; }
    public Guid? ListId { get; set; }
    public Guid? FolderUniqueId { get; set; }
    public int? ListItemId { get; set; }
    public long? Length { get; set; }
    public string? SiteUrl { get; set; }
    public string? WebUrl { get; set; }
    public string? ServerRelativeUrl { get; set; }
    public string? FileName { get; set; }
    public string? ContentTypeId { get; set; }
    public string? CustomizedPageStatus { get; set; }
    public string? SetupPath { get; set; }
    public string? FileReadState { get; set; }
    public string? FileErrorCodes { get; set; }
    public string? FileErrorDetail { get; set; }
    public string ProvenanceJson { get; set; } = "[]";
    public string? CoverageState { get; set; }
    public DateTime FirstObservedAtUtc { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    public string? ClientSideApplicationId { get; set; }
    public string? HtmlFileType { get; set; }
    public string? FileType { get; set; }
    public string? BSN { get; set; }
    public bool? WikiFieldHasValue { get; set; }
    public string? ItemMetadataReadState { get; set; }
    public string ItemMetadataFieldStatesJson { get; set; } = "{}";
    public string? NoExecuteRaw { get; set; }
    public string? NoExecuteSource { get; set; }
    public string? NoExecuteReadState { get; set; }
    public string? AspxEvidenceJson { get; set; }
}