using System.ComponentModel.DataAnnotations;

#nullable disable

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
