namespace PnP.Scanning.Core.Pipeline.Contracts.Site
{

    /// <summary>
    /// Carries site enumeration status and diagnostics in the existing site collection report shape.
    /// <para>Evolved from <c>Storage/Model/SiteCollection.cs</c> to preserve report fields while removing EF entities from offline analysis.</para>
    /// </summary>
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
