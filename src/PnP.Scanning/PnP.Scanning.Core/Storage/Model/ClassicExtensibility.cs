using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl), nameof(WebUrl) }, IsUnique = true)]
    internal class ClassicExtensibility : BaseScanResult
    {
        public bool UsesCustomMasterPage { get; set; }
        
        public string MasterPage { get; set; }

        public string CustomMasterPage { get; set; }

        public bool UsesCustomCSS { get; set; }

        public string AlternateCSS { get; set; }
        
        public bool UsesCustomTheme { get; set; }

        public bool UsesUserCustomAction { get; set; }

        public string RemediationCode { get; set; }

        internal bool AddToDatabase()
        {
            return true;
        }

    }
}
