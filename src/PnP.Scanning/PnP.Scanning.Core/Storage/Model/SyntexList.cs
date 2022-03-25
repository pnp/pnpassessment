using Microsoft.EntityFrameworkCore;

#nullable disable

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl), nameof(WebUrl), nameof(ListId) }, IsUnique = true)]
    internal sealed class SyntexList : BaseScanResult
    {
        #region List identification information
        public string ListServerRelativeUrl { get; set; }

        public string Title { get; set; }

        public Guid ListId { get; set; }

        public int ListTemplate { get; set; }

        public string ListTemplateString { get; set; }
        #endregion

        #region Information to determine Syntex need
        public bool AllowContentTypes { get; set; }

        public int ContentTypeCount { get; set; }

        public int FieldCount { get; set; }

        public string ListExperienceOptions { get; set; }

        public int WorkflowInstanceCount { get; set; }

        public int FlowInstanceCount { get; set; }

        public int RetentionLabelCount { get; set; }
        #endregion

        #region Information to collect list "activeness"
        public int ItemCount { get; set; }

        public int FolderCount { get; set; }

        public int DocumentCount { get; set; }

        public int AverageDocumentsPerFolder { get; set; }

        public string LibrarySize { get; set; }

        public bool UsesCustomColumns { get; set; }

        public DateTime Created { get; set; }

        public DateTime LastChanged { get; set; }

        public int LastChangedYear { get; set; }

        public int LastChangedMonth { get; set; }

        public string LastChangedMonthString { get; set; }

        public string LastChangedQuarter { get; set; }
        #endregion


    }
}
