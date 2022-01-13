namespace PnP.Scanning.Process.Commands
{
    internal class StartOptions
    {
        public AuthenticationMode AuthMode { get; set; }

        public string CertPath { get; set; }

        public FileInfo CertFile { get; set; }
    }
}
