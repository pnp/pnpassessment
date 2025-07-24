using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(nameof(ScanId), [nameof(SiteUrl), nameof(WebUrl), nameof(PageUrl), nameof(WebPartId)], IsUnique = true)]
    internal class ClassicWebPart : BaseScanResult
    {
        public string PageUrl { get; set; }

        public string PageName { get; set; }

        public string WebPartId { get; set; }

        public string WebPartType { get; set; }

        public string WebPartTitle { get; set; }

        public int WebPartZone { get; set; }

        public int WebPartZoneIndex { get; set; }

        public string WebPartProperties { get; set; }

        public bool IsClosed { get; set; }

        public bool IsHidden { get; set; }

        public string WebPartAssembly { get; set; }

        public string WebPartClass { get; set; }

        public bool HasProperMapping { get; set; }

        public string RemediationCode { get; set; }

        public Guid ListId { get; set; }

        public DateTime ModifiedAt { get; set; }
    }
}
