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

        public int FileCount { get; set; }

        public double FileCountMean { get; set; }

        public double FileCountStandardDeviation { get; set; }

        public double FileCountMin { get; set; }

        public double FileCountMax { get; set; }

        public double FileCountMedian { get; set; }

        public double FileCountLowerQuartile { get; set; }

        public double FileCountUpperQuartile { get; set; }

        public bool IsSyntexContentType { get; set; }

        public string SyntexModelDriveId { get; set; }

        public string SyntexModelObjectId { get; set; }


    }
}
