namespace PnP.Scanning.Core.Storage
{
    internal sealed class Scan
    {
        public Guid ScanId { get; set; }

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public ScanStatus Status { get; set; }

        public List<SiteCollection> SiteCollections { get; set; } = new();
    }
}
