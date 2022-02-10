using Microsoft.EntityFrameworkCore;

#nullable disable

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl) }, IsUnique = true)]
    internal sealed class SiteCollection
    {
        public Guid ScanId { get; set; }

        public string SiteUrl { get; set; }

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public SiteWebStatus Status { get; set; }

        public string Error { get; set; }

        public string StackTrace { get; set; }
    }
}
