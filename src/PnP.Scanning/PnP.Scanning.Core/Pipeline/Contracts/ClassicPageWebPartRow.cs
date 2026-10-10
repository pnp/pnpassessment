namespace PnP.Scanning.Core.Pipeline.Contracts
{
    /// <summary>
    /// Per-page web part inventory for a classic page. One row per web part discovered on a
    /// WebPart or Wiki page. Mirrors the old Modernization Scanner's per-web-part output.
    /// </summary>

    internal class ClassicPageWebPartRow : ClassicPageBaseRow
    {
        public string PageUrl { get; set; }

        public int WebPartIndex { get; set; }

        public string WebPartType { get; set; }

        public string WebPartTypeShort { get; set; }

        public string WebPartTitle { get; set; }

        // JSON serialized web part properties; only populated when ExportWebPartProperties is set.
        public string WebPartProperties { get; set; }

        public string ZoneId { get; set; }

        public int Row { get; set; }

        public int Column { get; set; }

        public int Order { get; set; }

        public bool Hidden { get; set; }

        public bool IsClosed { get; set; }

        public bool IsMappable { get; set; }
    }
}
