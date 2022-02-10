using Microsoft.EntityFrameworkCore;

#nullable disable

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl), nameof(WebUrl) }, IsUnique = true)]
    internal sealed class Web : BaseScanResult
    {
        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public SiteWebStatus Status { get; set; }

        public string Template { get; set; }

        public string Error { get; set; }

        public string StackTrace { get; set; }
    }
}
