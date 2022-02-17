#nullable disable

namespace PnP.Scanning.Core.Storage
{
    internal abstract class BaseScanResult
    {
        public Guid ScanId { get; set; }

        public string SiteUrl { get; set; }

        public string WebUrl { get; set; }
    }
}
