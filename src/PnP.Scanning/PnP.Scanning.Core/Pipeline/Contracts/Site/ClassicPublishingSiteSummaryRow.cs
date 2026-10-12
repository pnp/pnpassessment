namespace PnP.Scanning.Core.Pipeline.Contracts.Site
{
    /// <summary>
    /// Carries publishing-site web and page counts, used layouts and the latest saved page update date.
    /// <para>Evolved from <c>Storage/Model/ClassicPublishingSiteSummary.cs</c> to preserve report fields while removing EF entities from offline analysis.</para>
    /// </summary>

    internal sealed class ClassicPublishingSiteSummaryRow
    {
        public Guid ScanId { get; set; }

        public string SiteUrl { get; set; }

        // Number of publishing webs in this portal (webs flagged as a classic publishing site or that
        // carry at least one publishing page).
        public int NumberOfWebs { get; set; }

        // Total number of publishing pages across the portal's webs.
        public int NumberOfPages { get; set; }

        // Distinct, comma-separated custom (site) master pages used across the portal's publishing webs.
        public string UsedSiteMasterPages { get; set; }

        // Distinct, comma-separated system master pages used across the portal's publishing webs.
        public string UsedSystemMasterPages { get; set; }

        // Distinct, comma-separated page layouts used by the portal's publishing pages.
        public string UsedPageLayouts { get; set; }

        // Most recent modification date across the portal's publishing pages (null when none were scanned).
        public DateTime? LastPageUpdateDate { get; set; }
    }
}
