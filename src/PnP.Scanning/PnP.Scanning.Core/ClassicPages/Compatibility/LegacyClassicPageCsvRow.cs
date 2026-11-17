#nullable enable
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.ClassicPages.Compatibility;

/// <summary>
/// Preserves the historical page CSV fields and adds retained audit evidence for the current report template.
/// This is an export row, not an EF entity or a reconstructed native Page.
/// </summary>
internal sealed class LegacyClassicPageCsvRow(ClassicPage page)
{
    public string PageUrl => page.PageUrl;
    public string PageName => page.PageName;
    public string PageType => page.PageType;
    public string ListUrl => page.ListUrl;
    public string ListTitle => page.ListTitle;
    public Guid ListId => page.ListId;
    public Guid? SiteCollectionId => page.SiteCollectionId;
    public Guid? WebId => page.WebId;
    public Guid? FileUniqueId => page.FileUniqueId;
    public int? ListItemId => page.ListItemId;
    public string DiscoveryStatus => page.DiscoveryStatus;
    public string AssessmentStatus => page.AssessmentStatus;
    public DateTime ModifiedAt => page.ModifiedAt;
    public string Layout => page.Layout;
    public string CustomizedPageStatus => page.CustomizedPageStatus;
    public string LayoutUrl => page.LayoutUrl;
    public string LayoutCustomizedPageStatus => page.LayoutCustomizedPageStatus;
    public bool? HomePage => page.HomePage;
    public bool UncustomizedHomePage => page.UncustomizedHomePage;
    public string ModifiedBy => page.ModifiedBy;
    public int WebPartCount => page.WebPartCount;
    public double MappingPercentage => page.MappingPercentage;
    public string UnmappedWebParts => page.UnmappedWebParts;
    public string RemediationCode => page.RemediationCode;
    public Guid ScanId => page.ScanId;
    public string SiteUrl => page.SiteUrl;
    public string WebUrl => page.WebUrl;

    public int? AuditViewsCount { get; internal set; }
    public int? AuditCreatesCount { get; internal set; }
    public int? AuditEditsCount { get; internal set; }
    public int? AuditUniqueUsers { get; internal set; }
    public DateTime? AuditWindowStart { get; internal set; }
    public DateTime? AuditWindowEnd { get; internal set; }
    public string UsageQueryStatus { get; internal set; } = "notcollected";
    public string UsageAttributionState { get; internal set; } = "NotAcquired";
}
