#nullable enable
namespace PnP.Scanning.Core.Storage;

/// <summary>Raw observations of one physical SharePoint file, keyed only by its real site-collection and file GUIDs.</summary>
internal sealed class SPFile
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
    public string FileReadState { get; set; } = ClassicAspxReadStates.NotAttempted;
    public string? FileErrorCodes { get; set; }
    public string? FileErrorDetail { get; set; }
    public string ProvenanceJson { get; set; } = "[]";
    public string CoverageState { get; set; } = ClassicAspxCoverageStates.Unknown;
    public DateTime FirstObservedAtUtc { get; set; }
    public DateTime ObservedAtUtc { get; set; }
    public string? ClientSideApplicationId { get; set; }
    public string? HtmlFileType { get; set; }
    public string? FileType { get; set; }
    public string? BSN { get; set; }
    public bool? WikiFieldHasValue { get; set; }
    public string ItemMetadataReadState { get; set; } = ClassicAspxReadStates.NotAttempted;
    public string ItemMetadataFieldStatesJson { get; set; } = "{}";
    /// <summary>Original public NoExecute field text, normally 0/1; this is not a complete server-side document-flags mask.</summary>
    public string? NoExecuteRaw { get; set; }
    public string? NoExecuteSource { get; set; }
    public string NoExecuteReadState { get; set; } = ClassicAspxReadStates.NotAttempted;
}
