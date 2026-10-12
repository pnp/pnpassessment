using PnP.Scanning.Core.Pipeline.Contracts.Shared;
namespace PnP.Scanning.Core.Pipeline.Contracts.Audit
{
    /// <summary>
    /// Carries per-page audit counts, the fixed query window and coverage diagnostics for report publication.
    /// <para>Evolved from <c>Storage/Model/ClassicPageAuditUsage.cs</c> to preserve report fields while removing EF entities from offline analysis.</para>
    /// </summary>
    internal class ClassicPageAuditUsageRow : ClassicPageBaseRow
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
