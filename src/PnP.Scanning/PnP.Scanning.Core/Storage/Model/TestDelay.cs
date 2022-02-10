using Microsoft.EntityFrameworkCore;

#nullable disable

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl), nameof(WebUrl) }, IsUnique = true)]
    internal sealed class TestDelay : BaseScanResult
    {
        public int Delay1 { get; set; }

        public int Delay2 { get; set; }

        public int Delay3 { get; set; }

        public string WebIdString { get; set; }
    }
}
