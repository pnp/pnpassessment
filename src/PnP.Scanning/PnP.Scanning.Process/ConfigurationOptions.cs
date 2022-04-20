namespace PnP.Scanning.Process
{
    internal sealed class ConfigurationOptions
    {
        public string Environment { get; set; }

        public int Port { get; set; }

        public string AdminCenterUrl { get; set; }

        public string MySiteHostUrl { get; set; }
    }
}
