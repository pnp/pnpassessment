namespace PnP.Scanning.Core.Storage
{
    internal sealed class Cache
    {
        public Guid ScanId { get; set; }

        public string? Key { get; set; }

        public string? Value { get; set; }
    }
}
