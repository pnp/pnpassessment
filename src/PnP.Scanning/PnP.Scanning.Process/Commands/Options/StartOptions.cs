namespace PnP.Scanning.Process.Commands
{
    internal class StartOptions
    {
        public Mode Mode { get; set; }

        public AuthenticationMode AuthMode { get; set; }

        public string? CertPath { get; set; }

        public FileInfo? CertFile { get; set; }
    }
}
