namespace PnP.Scanning.Core.Storage
{
    [Microsoft.EntityFrameworkCore.Index(nameof(ScanId), [nameof(SiteUrl)], IsUnique = true)]
    internal sealed class ClassicSiteSummary
    {
        public Guid ScanId { get; set; }

        public string SiteUrl { get; set; }

        public DateTime LastItemUserModifiedDate { get; set; }
        
        public string RootWebTemplate { get; set; }

        public string SubWebTemplates { get; set; }

        public int SubWebCount { get; set; }

        public int SubWebDepth { get; set; }

        public int ClassicLists { get; set; }

        public int ModernLists { get; set; }

        public int ClassicPages { get; set; }

        public int ClassicWikiPages { get; set; }

        public int ClassicASPXPages { get; set; }

        public int ClassicBlogPages { get; set; }

        public int ClassicWebPartPages { get; set; }

        public int ClassicPublishingPages { get; set; }

        public int ModernPages { get; set; }

        public int ClassicWorkflows { get; set; }

        public int ClassicInfoPathForms { get; set; }
        
        public int ClassicExtensibilities { get; set; }
        
        public int SharePointAddIns { get; set; }

        public int AzureACSPrincipals { get; set; }

        public string AggregatedRemediationCodes { get; set; }

    }
}
