using PnP.Core.Services;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Process.Commands
{
    internal class StartOptions
    {
        public Mode Mode { get; set; }

        public string Tenant { get; set; }

        public List<string> SitesList { get; set; }

        public FileInfo SitesFile { get; set; }

        public AuthenticationMode AuthMode { get; set; }

        public Guid ApplicationId { get; set; }

        public string TenantId { get; set; }

        public string CertPath { get; set; }

        public FileInfo CertFile { get; set; }

        public string CertPassword { get; set; }

        public int Threads { get; set; }

        // PER SCAN COMPONENT: implement scan component specific options
        public bool SyntexDeepScan { get; set; }

        public bool WorkflowAnalyze { get; set; }

#if DEBUG
        public int TestNumberOfSites { get; set; }
#endif
    }
}
