using PnP.Scanning.Core.ClassicPages.Contracts.Shared;

#nullable enable annotations


namespace PnP.Scanning.Core.ClassicPages.Contracts.Page
{
    /// <summary>
    /// Carries detached page identity, readiness, classification, and acquisition provenance.
    /// </summary>
    internal class ClassicPageRow : ClassicPageBaseRow
    {
        public PageIdentity? Identity { get; set; }
        public string? AssessmentPageType { get; set; }
        public string? ContentReadState { get; set; }
        public string? ClassificationReason { get; set; }
        public string? PageUrl { get; set; }

        public string? PageName { get; set; }

        public string PageType { get; set; } = "Unknown";

        public string? ListUrl { get; set; }

        public string? ListTitle { get; set; }

        public Guid? ListId { get; set; }

        public Guid? SiteCollectionId { get; set; }
        public Guid? WebId { get; set; }
        public Guid? FileUniqueId { get; set; }
        public int? ListItemId { get; set; }
        public string DiscoveryStatus { get; set; }
        public string AssessmentStatus { get; set; }
        public string? CustomizedPageStatus { get; set; }
        public string? LayoutUrl { get; set; }
        public string? LayoutCustomizedPageStatus { get; set; }

        public DateTime ModifiedAt { get; set; }

        // Page transformation readiness enrichment (ported from the Modernization Scanner)
        public string? Layout { get; set; }

        public bool? HomePage { get; set; }

        public bool UncustomizedHomePage { get; set; }

        public string? ModifiedBy { get; set; }

        // Page transformation readiness rollup (computed from the page's web part inventory)
        public int WebPartCount { get; set; }

        public double MappingPercentage { get; set; }

        public string? UnmappedWebParts { get; set; }

        public string? RemediationCode { get; set; }

#nullable enable
        public string Experience { get; set; } = "Unknown";
        public string ProductFamily { get; set; } = "Unknown";
        public string? ImpactedBy { get; set; }
        public string ImpactStatus { get; set; } = "Unknown";
        public int? AuditViewsCount { get; set; }
        public int? AuditUniqueUsers { get; set; }
        public DateTime? AuditWindowStart { get; set; }
        public DateTime? AuditWindowEnd { get; set; }
        public string UsageQueryStatus { get; set; } = "notcollected";
        public string SourceKind { get; set; } = "Unknown";
        public int? ListTemplate { get; set; }
    }
}