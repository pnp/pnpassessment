using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl), nameof(WebUrl), nameof(ListId) }, IsUnique = true)]
    internal class InfoPath : BaseScanResult
    {
        public string ListUrl { get; set; }

        public string ListTitle { get; set; }

        public Guid ListId { get; set; }

        /// <summary>
        ///  Indicates how InfoPath is used here: form library or customization of the list form pages
        /// </summary>
        public string InfoPathUsage { get; set; }

        public string InfoPathTemplate { get; set; }

        public bool Enabled { get; set; }

        public int ItemCount { get; set; }

        public DateTime LastItemUserModifiedDate { get; set; }
    }
}
