using PnP.Scanning.Core.ClassicPages.Contracts.Shared;


namespace PnP.Scanning.Core.ClassicPages.Contracts.Page
{
    /// <summary>
    /// Carries an ordered detached web-part inventory and its readiness verdict.
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