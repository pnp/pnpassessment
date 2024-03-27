using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(nameof(ScanId), [nameof(SiteUrl), nameof(WebUrl)], IsUnique = true)]
    internal class ClassicWebSummary : BaseScanResult
    {
        public string Template { get; set; }

        public DateTime LastItemUserModifiedDate { get; set; }

        public int ClassicLists { get; set; }

        public int ModernLists { get; set; }

        public int ClassicPages { get; set; }

        public int ClassicWikiPages { get; set; }

        public int ClassicASPXPages { get; set; }

        public int ClassicBlogPages { get; set; }

        public int ClassicWebPartPages { get; set; }

        public int ClassicPublishingPages { get; set; }

        public int ModernPages { get; set; }

        public bool IsModernSite { get; set; }

        public bool IsClassicPublishingSite { get; set; }

        public bool IsModernCommunicationSite { get; set; }

        public bool HasClassicWorkflow { get; set; }

        public int ClassicWorkflows { get; set; }

        public bool HasClassicInfoPathForms { get; set; }

        public int ClassicInfoPathForms { get; set; }

        public bool HasClassicExtensibility { get; set; }

        public int ClassicExtensibilities { get; set; }

        public bool HasSharePointAddIns { get; set; }

        public int SharePointAddIns { get; set; }

        public bool HasAzureACSPrincipal { get; set; }

        public int AzureACSPrincipals { get; set; }

        public string RemediationCode { get; set; }

        public string AggregatedRemediationCodes { get; set; }

    }
}
