namespace PnP.Scanning.Core.Storage
{
    internal sealed class Web : BaseScanResult
    {
        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public SiteWebStatus Status { get; set; }

        public string? Error { get; set; }

        public string? StackTrace { get; set; }
    }
}
