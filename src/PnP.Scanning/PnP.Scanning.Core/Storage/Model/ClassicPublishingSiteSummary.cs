namespace PnP.Scanning.Core.Storage
{
    /// <summary>
    /// Per-site-collection publishing-portal rollup. Mirrors the legacy Modernization Scanner's
    /// <c>ModernizationPublishingSiteScanResults.csv</c> (one row per publishing portal), consolidating the
    /// publishing webs + publishing pages of a site collection into a single readiness line. Built in
    /// <see cref="StorageManager.PopulatePublishingSiteSummaryAsync"/> during post-scan processing.
    /// </summary>
    [Microsoft.EntityFrameworkCore.Index(nameof(ScanId), [nameof(SiteUrl)], IsUnique = true)]
    internal sealed class ClassicPublishingSiteSummary
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
