using PnP.Scanning.Core.Pipeline.Contracts.Shared;
namespace PnP.Scanning.Core.Pipeline.Contracts.Page
{
    /// <summary>
    /// Carries a detached web part inventory row with page identity, layout position and optional captured properties.
    /// <para>Evolved from <c>Storage/Model/ClassicPageWebPart.cs</c> to preserve report fields while removing EF entities from offline analysis.</para>
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
