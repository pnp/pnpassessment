using CsvHelper.Configuration.Attributes;

#nullable disable

namespace PnP.Scanning.Core.Storage
{
    [Microsoft.EntityFrameworkCore.Index(new string[] { nameof(ScanId), nameof(SiteUrl) }, IsUnique = true)]
    internal sealed class SiteCollection
    {
        public Guid ScanId { get; set; }

        public string SiteUrl { get; set; }

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public int ScanDuration { get; set; }

        public SiteWebStatus Status { get; set; }

        [Ignore]
        public string Error { get; set; }

        [Ignore]
        public string StackTrace { get; set; }
    }
}
