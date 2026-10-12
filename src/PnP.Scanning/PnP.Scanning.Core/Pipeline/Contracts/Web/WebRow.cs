using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Site;
namespace PnP.Scanning.Core.Pipeline.Contracts.Web
{

    /// <summary>
    /// Carries web identity, template, acquisition status and error details in the existing web report shape.
    /// <para>Evolved from <c>Storage/Model/Web.cs</c> to preserve report fields while removing EF entities from offline analysis.</para>
    /// </summary>
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
