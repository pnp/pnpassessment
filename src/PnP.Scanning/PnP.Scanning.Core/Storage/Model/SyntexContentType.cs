using Microsoft.EntityFrameworkCore;

#nullable disable

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl), nameof(WebUrl), nameof(ListId), nameof(ContentTypeId) }, IsUnique = true)]
    internal sealed class SyntexContentType: BaseScanResult
    {
        public Guid ListId { get; set; }

        public string ContentTypeId { get; set; }

        public string Name { get; set; }

        public string Group { get; set; }

        public bool Hidden { get; set; }

        public int FieldCount { get; set; }

        public bool IsSyntexContentType { get; set; }

    }
}
