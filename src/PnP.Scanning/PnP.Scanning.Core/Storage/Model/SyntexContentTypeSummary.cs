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

        public int ListCount { get; set; }

        public int ItemCount { get; set; }

        public double ItemCountMean { get; set; }

        public double ItemCountStandardDeviation { get; set; }

        public double ItemCountMin { get; set; }

        public double ItemCountMax { get; set; }

        public double ItemCountMedian { get; set; }

        public double ItemCountLowerQuartile { get; set; }

        public double ItemCountUpperQuartile { get; set; }

        public bool IsSyntexContentType { get; set; }

        public string SyntexModelDriveId { get; set; }

        public string SyntexModelObjectId { get; set; }


    }
}
