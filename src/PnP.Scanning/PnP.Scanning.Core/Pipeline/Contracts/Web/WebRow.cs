using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Site;
namespace PnP.Scanning.Core.Pipeline.Contracts.Web
{

    internal sealed class WebRow : ClassicPageBaseRow
    {
        public string WebUrlAbsolute { get; set; }

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public int ScanDuration { get; set; }

        public ClassicPageSiteStatus Status { get; set; }

        public string Template { get; set; }

        public string Error { get; set; }

        public string StackTrace { get; set; }
    }
}
