using System.ComponentModel.DataAnnotations;

namespace PnP.Scanning.Core.Storage
{
    internal sealed class Scan
    {
        [Key]
        public Guid ScanId { get; set; }

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public ScanStatus Status { get; set; }

        public SiteWebStatus PreScanStatus { get; set; }

        public SiteWebStatus PostScanStatus { get; set; }

        public string Version { get; set; }

        // Zero is historical authority. Only LaunchNewScanAsync initializes the current rule.
        public int PublishingLayoutRuleVersion { get; set; }

        public string PublishingLayoutTypeCatalogJson { get; set; }

        // Zero means CP1 evidence was not enabled for this scan, including inherited scans.
        // Only new-scan initialization freezes the current contract and configuration snapshot.
        public int PageSourceEvidenceVersion { get; set; }

        public string PageBaseTypeConfigurationJson { get; set; }

        public string CLIMode { get; set; }

        public string CLITenant { get; set; }

        public string CLITenantId { get; set; }

        public string CLIEnvironment { get; set; }

        public string CLISiteList { get; set; }   

        public string CLISiteFile { get; set; }

        public string CLIAuthMode { get; set; }

        public string CLIApplicationId { get; set; }

        public string CLICertPath { get; set; }

        public string CLICertFile { get; set; }

        public string CLICertFilePassword { get; set; }

        public int CLIThreads { get; set; }
    }
}
