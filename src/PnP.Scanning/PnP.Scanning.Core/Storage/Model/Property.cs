using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(nameof(ScanId), [nameof(Name)], IsUnique = true)]
    internal sealed class Property
    {
        public Guid ScanId { get; set; }

        public string Name { get; set; }

        public string Type { get; set; }

        public string Value { get; set; }
    }
}
