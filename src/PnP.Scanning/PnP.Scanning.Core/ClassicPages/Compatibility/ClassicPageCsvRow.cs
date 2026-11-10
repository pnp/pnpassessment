#nullable enable
using CsvHelper.Configuration.Attributes;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;

namespace PnP.Scanning.Core.ClassicPages.Compatibility;

/// <summary>
/// Flattens one canonical native Page for the existing CSV filename while exposing unavailable measurements as null.
/// </summary>
internal sealed class ClassicPageCsvRow
{
    [Ignore] private readonly ClassicPageRow page;
    [Ignore] internal AspxCsvRow? File { get; }
    [Ignore] internal PageIdentity Identity { get; }

    /// <summary>
    /// Preserves canonical identity/readiness and names an admitted ASPX by its exact handler rather than metadata heuristics.
    /// </summary>
    internal ClassicPageCsvRow(
        ClassicPageRow page,
        AspxCsvRow? file,
        string pageType,
        Guid analysisRunId,
        Guid snapshotId)
    {
        this.page = page;
        Identity = page.Identity?.Validated() ??
                   throw new InvalidDataException("A CSV Page requires observed native identity.");
        File = file;
        PageType = pageType;
        AnalysisRunId = analysisRunId;
        SnapshotId = snapshotId;
    }

    public Guid ScanId => page.ScanId;
    public string SiteUrl => page.SiteUrl;
    public string WebUrl => page.WebUrl;
    public string? PageUrl => page.PageUrl;
    public string? PageName => page.PageName;
    public string PageType { get; }
    public string? AssessmentPageType => page.AssessmentPageType ?? page.PageType;
    public string? ListUrl => page.ListUrl;
    public string? ListTitle => page.ListTitle;
    public Guid? ListId => page.ListId;
    public Guid? SiteCollectionId => page.SiteCollectionId;
    public Guid? WebId => page.WebId;
    public Guid? FileUniqueId => page.FileUniqueId;
    public int? ListItemId => page.ListItemId;
    public string ObjectKind => Identity.ObjectKind.ToString();
    public string ContentKey => Identity.Key;
    public string SourceKind => page.SourceKind;
    public int? ListTemplate => File?.ListTemplate ?? page.ListTemplate;
    public string DiscoveryStatus => page.DiscoveryStatus;
    public string AssessmentStatus => page.AssessmentStatus;
    public DateTime? ModifiedAt => page.ModifiedAt == default ? null : page.ModifiedAt;
    public string? ModifiedBy => page.ModifiedBy;
    public string? Layout => page.Layout;
    public string? CustomizedPageStatus => page.CustomizedPageStatus;
    public string? LayoutUrl => page.LayoutUrl;
    public string? LayoutCustomizedPageStatus => page.LayoutCustomizedPageStatus;
    public bool? HomePage => page.HomePage;
    public bool? UncustomizedHomePage { get; internal set; }
    public int? WebPartCount => HasPartAssessment ? page.WebPartCount : null;
    public double? MappingPercentage => HasPartAssessment ? page.MappingPercentage : null;
    public string? UnmappedWebParts => HasPartAssessment ? page.UnmappedWebParts : null;
    public string? RemediationCode => page.RemediationCode;
    public string Experience => page.Experience;
    public string ProductFamily => page.ProductFamily;
    public string ImpactStatus => page.ImpactStatus;
    public string? ImpactedBy => page.ImpactedBy;
    public string? ClassificationReason => page.ClassificationReason;
    public string? PageHandler => File?.PageHandler;
    public bool? NoExecute => File?.NoExecute;
    public string? NoExecuteRaw => File?.NoExecuteRaw;
    public string? NoExecuteReadState => File?.NoExecuteReadState;
    public string? SourceSha256 => File?.SourceSha256;
    public string? SourceReadState => File?.SourceReadState;
    public string? HandlerResolution => File?.HandlerResolution;
    public string? ContentReadState => page.ContentReadState;
    public bool? IsCatalog => File?.IsCatalog;
    public bool? IsListView => File?.IsListView;
    public bool? IsListForm => File?.IsListForm;
    public string? RoleReadState => File?.RoleReadState;
    public bool? Official => File?.Official;
    public string OfficialReadState => File?.OfficialReadState ?? "NotApplicable";
    public int? AuditViewsCount { get; internal set; }
    public int? AuditCreatesCount { get; internal set; }
    public int? AuditEditsCount { get; internal set; }
    public int? AuditUniqueUsers { get; internal set; }
    public DateTime? AuditWindowStart { get; internal set; }
    public DateTime? AuditWindowEnd { get; internal set; }
    public string UsageQueryStatus { get; internal set; } = "notcollected";
    public Guid AnalysisRunId { get; }
    public Guid SnapshotId { get; }

    [Ignore]
    private bool HasPartAssessment =>
        Identity.ObjectKind == PageObjectKind.File && page.AssessmentStatus == "Complete" &&
        (page.AssessmentPageType ?? page.PageType) is "WikiPage" or "WebPartPage" or "PublishingPage";
}