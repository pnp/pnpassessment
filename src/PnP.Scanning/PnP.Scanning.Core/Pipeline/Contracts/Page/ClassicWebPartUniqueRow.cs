namespace PnP.Scanning.Core.Pipeline.Contracts.Page
{
    /// <summary>
    /// Summarizes one web part type, the number of pages using it and whether the mapping catalog recognizes it.
    /// <para>Evolved from <c>Storage/Model/ClassicWebPartUnique.cs</c> to preserve report fields while removing EF entities from offline analysis.</para>
    /// </summary>

    internal sealed class ClassicWebPartUniqueRow
    {
        public Guid ScanId { get; set; }

        public string WebPartType { get; set; }

        public bool InMappingFile { get; set; }

        public int PageCount { get; set; }
    }
}
