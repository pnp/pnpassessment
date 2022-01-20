namespace PnP.Scanning.Core.Storage
{
    internal sealed class SiteCollection
    {
        public Guid ScanId { get; set; }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public string SiteUrl { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public ScanStatus Status { get; set; }

        public List<Web> Webs { get; set; } = new();
    }
}
