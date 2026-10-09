namespace PnP.Scanning.Core.Pipeline.Contracts
{
    /// <summary>
    /// Scan-wide unique web part inventory: one row per distinct web part type seen across the
    /// whole scan, with whether it exists in the mapping file and how many pages reference it.
    /// Mirrors the old Modernization Scanner's UniqueWebParts.csv.
    /// </summary>

    internal sealed class ClassicWebPartUniqueRow
    {
        public Guid ScanId { get; set; }

        public string WebPartType { get; set; }

        public bool InMappingFile { get; set; }

        public int PageCount { get; set; }
    }
}
