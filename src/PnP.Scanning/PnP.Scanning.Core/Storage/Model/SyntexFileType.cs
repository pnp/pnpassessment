using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(nameof(ScanId), [nameof(SiteUrl), nameof(WebUrl), nameof(ListId), nameof(FileType)], IsUnique = true)]
    [Index(nameof(ScanId), [nameof(FileType)])]
    internal sealed class SyntexFileType: BaseScanResult
    {
        public Guid ListId { get; set; }

        public string FileType { get; set; }

        public int ItemCount { get; set; }
    }
}
