using Microsoft.EntityFrameworkCore;

#nullable disable

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl), nameof(WebUrl), nameof(Classifier), nameof(TargetSiteId), nameof(TargetWebId), nameof(TargetListId) })]
    internal sealed class SyntexModelUsage: BaseScanResult
    {
        public string Classifier { get; set; }

        public Guid TargetSiteId { get; set; }

        public Guid TargetWebId { get; set; }

        public Guid TargetListId { get; set; }

        public int ClassifiedItemCount { get; set; }

        public int NotProcessedItemCount { get; set; }
        
        public double AverageConfidenceScore { get; set; }

    }
}
