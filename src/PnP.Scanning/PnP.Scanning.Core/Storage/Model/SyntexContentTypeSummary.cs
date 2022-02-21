using Microsoft.EntityFrameworkCore;

#nullable disable

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(ContentTypeId) }, IsUnique = true)]
    internal sealed class SyntexContentTypeSummary
    {
        public Guid ScanId { get; set; }

        public string ContentTypeId { get; set; }

        public string Name { get; set; }

        public string Group { get; set; }

        public bool Hidden { get; set; }

        public int FieldCount { get; set; }

        public bool IsSyntexContentType { get; set; }

        public string SyntexModelDriveId { get; set; }

        public string SyntexModelObjectId { get; set; }

        public int Count { get; set; }
                
        public int FileCount { get; set; }

    }
}
