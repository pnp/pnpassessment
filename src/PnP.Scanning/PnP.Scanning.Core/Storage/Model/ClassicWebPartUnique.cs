using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    /// <summary>
    /// Scan-wide unique web part inventory: one row per distinct web part type seen across the
    /// whole scan, with whether it exists in the mapping file and how many pages reference it.
    /// Mirrors the old Modernization Scanner's UniqueWebParts.csv.
    /// </summary>
    [Index(nameof(ScanId), [nameof(WebPartType)], IsUnique = true)]
    internal sealed class ClassicWebPartUnique
    {
        public Guid ScanId { get; set; }

        public string WebPartType { get; set; }

        public bool InMappingFile { get; set; }

        public int PageCount { get; set; }
    }
}
