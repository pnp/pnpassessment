using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(nameof(ScanId), [nameof(SiteUrl), nameof(WebUrl), nameof(ListId)], IsUnique = true)]
    internal class ClassicList : BaseScanResult
    {
        public Guid ListId { get; set; }

        public string ListUrl { get; set; }

        public string ListTitle { get; set; }
        
        public string ListTemplateType { get; set; }

        public string ListTemplate { get; set; }

        public string ListExperience { get; set; }

        public bool ClassicByDesign { get; set; }

        public string DefaultViewRenderType { get; set; }

        public DateTime LastModifiedAt { get; set; }

        public int ItemCount { get; set; }

        public string RemediationCode { get; set; }

        internal bool AddToDatabase()
        {
            // If a list uses a template which was never modernized
            if (ClassicByDesign)
            {
                return true;
            }

            // The default list view tells the list cannot be shown as modern list
            if (DefaultViewRenderType != PnP.Core.Model.SharePoint.ListPageRenderType.Modern.ToString())
            {
                return true;
            }

            // The list experience is forcifully set on classic
            if (ListExperience == PnP.Core.Model.SharePoint.ListExperience.ClassicExperience.ToString())
            {
                return true;
            }

            return false;
        }

    }
}
