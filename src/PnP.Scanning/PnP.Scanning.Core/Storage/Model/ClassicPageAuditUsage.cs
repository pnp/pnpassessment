namespace PnP.Scanning.Core.Storage
{
    internal class ClassicPageAuditUsage : BaseScanResult
    {
        public string PageUrl { get; set; }
        public int AuditViewsCount { get; set; }    // ClassicPageViewed
        public int AuditCreatesCount { get; set; }  // ClassicPageCreated
        public int AuditEditsCount { get; set; }    // ClassicPageEdited
        public int AuditUniqueUsers { get; set; }   // distinct users across all operations
        public DateTime AuditWindowStart { get; set; }
        public DateTime AuditWindowEnd { get; set; }
        public string QueryStatus { get; set; }
        public string SkipReason { get; set; }
    }
}
