namespace PnP.Scanning.Core.Storage
{
    internal sealed class SiteCollection
    {
        public Guid ScanId { get; set; }

        public string? SiteUrl { get; set; }

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public SiteWebStatus Status { get; set; }
    }
}
