namespace PnP.Scanning.Core.Storage
{
    internal sealed class Property
    {
        public Guid ScanId { get; set; }

        public string? Name { get; set; }

        public string? Type { get; set; }

        public string? Value { get; set; }
    }
}
