using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl), nameof(WebUrl), nameof(PageUrl) }, IsUnique = true)]
    internal class Page : BaseScanResult
    {
        public string PageUrl { get; set; }

        public string PageName { get; set; }
        
        public string PageType { get; set; }
        
        public string ListUrl { get; set; }

        public string ListTitle { get; set; }

        public Guid ListId { get; set; }

        public DateTime ModifiedAt { get; set; }
    }
}
