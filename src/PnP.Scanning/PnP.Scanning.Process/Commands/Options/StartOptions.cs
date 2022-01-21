namespace PnP.Scanning.Process.Commands
{
    internal class StartOptions
    {
        public Mode Mode { get; set; }

        public string? Tenant { get; set; }

        public Microsoft365Environment Environment { get; set; }

        public List<string>? SitesList { get; set; }

        public FileInfo? SitesFile { get; set; }

        public AuthenticationMode AuthMode { get; set; }

        public Guid ApplicationId { get; set; }

        public string? CertPath { get; set; }

        public FileInfo? CertFile { get; set; }

        public string? CertPassword { get; set; }

#if DEBUG
        public int TestNumberOfSites { get; set; }
#endif
    }
}
