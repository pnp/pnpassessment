namespace PnP.Scanning.Core.Pipeline.Contracts.Site
{

    internal sealed class SiteCollectionRow
    {
        public Guid ScanId { get; set; }

        public string SiteUrl { get; set; }

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public int ScanDuration { get; set; }

        public ClassicPageSiteStatus Status { get; set; }

        public string Error { get; set; }

        public string StackTrace { get; set; }
    }
}
