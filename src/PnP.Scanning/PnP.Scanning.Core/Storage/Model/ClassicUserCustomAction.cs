using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl), nameof(WebUrl), nameof(Id) }, IsUnique = true)]
    internal class ClassicUserCustomAction : BaseScanResult
    {
        public Guid Id { get; set; }

        public string Title { get; set; }

        public string Name { get; set; }

        public string Location { get; set; }

        public string RegistrationType { get; set; }

        public string RegistrationId { get; set; }

        public string CommandAction { get; set; }

        public string CommandUIExtension { get; set; }

        public string Description { get; set; }
        
        public string ScriptBlock { get; set; }

        public string ScriptSrc { get; set; }

        public string Url { get; set; }

        public string Problem { get; set; }

        public Guid ListId { get; set; }

        public string ListUrl { get; set; }

        public string ListTitle { get; set; }

        public string RemediationCode { get; set; }

        public bool AddToDatabase()
        {
            return !string.IsNullOrEmpty(Problem);
        }
    }
}
