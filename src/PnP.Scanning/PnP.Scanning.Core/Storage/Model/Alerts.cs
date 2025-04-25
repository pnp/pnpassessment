using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(nameof(ScanId), [nameof(SiteUrl), nameof(WebUrl), nameof(AlertId)], IsUnique = true)]
    internal class Alerts: BaseScanResult
    {
        public Guid AlertId { get; set; }

        public string AlertTitle { get; set; }

        public string AlertType { get; set; }

        public string Status { get; set; }

        public string DeliveryChannel { get; set; }

        public string EventType { get; set; }

        public string AlertFrequency { get; set; }

        public string CAMLQuery { get; set; }

        public string Filter { get; set; }

        public string UserLoginName { get; set; }

        public string UserName { get; set; }

        public string UserPrincipalType { get; set; }

        public string UserEmail { get; set; }

        public string ListUrl { get; set; }

        public Guid ListId { get; set; }

        public string ListTitle { get; set; }

        public string AlertTemplateName { get; set; }

        public DateTime AlertTime { get; set; }

        public int ListItemId { get; set; }

    }
}
