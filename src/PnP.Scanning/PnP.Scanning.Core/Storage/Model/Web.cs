namespace PnP.Scanning.Core.Storage
{
    internal sealed class Web
    {
        public Guid ScanId { get; set; }

        public string? SiteUrl { get; set; }

        public string? WebUrl { get; set; }

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public ScanStatus Status { get; set; }

    }
}
