using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Scanners;
using IgnoreAttribute = CsvHelper.Configuration.Attributes.IgnoreAttribute;

namespace PnP.Scanning.Core.Storage
{
    [Index(nameof(ScanId), [nameof(SiteUrl), nameof(WebUrl), nameof(PageUrl)], IsUnique = true)]
    internal class ClassicPage : BaseScanResult
    {
        public string PageUrl { get; set; }

        public string PageName { get; set; }
        
        public string PageType { get; set; }
        
        public string ListUrl { get; set; }

        public string ListTitle { get; set; }

        public Guid ListId { get; set; }

        public Guid? SiteCollectionId { get; set; }
        public Guid? WebId { get; set; }
        public Guid? FileUniqueId { get; set; }
        public int? ListItemId { get; set; }
        public string DiscoveryStatus { get; set; }
        public string AssessmentStatus { get; set; }

        public DateTime ModifiedAt { get; set; }

        // Page transformation readiness enrichment (ported from the Modernization Scanner)
        public string Layout { get; set; }

        public bool? HomePage { get; set; }

        public bool UncustomizedHomePage { get; set; }

        public string ModifiedBy { get; set; }

        // Page transformation readiness rollup (computed from the page's web part inventory)
        public int WebPartCount { get; set; }

        public double MappingPercentage { get; set; }

        public string UnmappedWebParts { get; set; }

        public string RemediationCode { get; set; }

        // The declared ASPX type, or a short ERROR value when source acquisition/parsing failed.
        public string PageHandler { get; set; }

        // Provenance stays in SQLite. The page CSV exposes only the PageHandler display value.
        [Ignore]
        public string PageHandlerEvidenceJson { get; set; }

        public bool AddToDatabase()
        {
            if (PageType == PageScanComponent.ModernPage)
            {
                return false;
            }
            else
            {
                return true;
            }
        }
    }
}
