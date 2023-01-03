using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl), nameof(WebUrl), nameof(PageUrl) }, IsUnique = true)]
    internal class ClassicPage : BaseScanResult
    {
        public string PageUrl { get; set; }

        public string PageName { get; set; }
        
        public string PageType { get; set; }
        
        public string ListUrl { get; set; }

        public string ListTitle { get; set; }

        public Guid ListId { get; set; }

        public DateTime ModifiedAt { get; set; }

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
