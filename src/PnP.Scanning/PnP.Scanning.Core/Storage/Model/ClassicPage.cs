using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Scanners;

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

        public DateTime ModifiedAt { get; set; }

        // Page transformation readiness enrichment (ported from the Modernization Scanner)
        public string Layout { get; set; }

        public bool HomePage { get; set; }

        public bool UncustomizedHomePage { get; set; }

        public string ModifiedBy { get; set; }

        // Page transformation readiness rollup (computed from the page's web part inventory)
        public int WebPartCount { get; set; }

        public double MappingPercentage { get; set; }

        public string UnmappedWebParts { get; set; }

        public string RemediationCode { get; set; }

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
