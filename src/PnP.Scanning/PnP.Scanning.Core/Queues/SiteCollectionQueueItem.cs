using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Queues
{
    internal class SiteCollectionQueueItem : QueueItemBase
    {
        internal SiteCollectionQueueItem(OptionsBase optionsBase, string siteCollectionUrl) : base(optionsBase)
        {
            SiteCollectionUrl = siteCollectionUrl;
        }

        internal string SiteCollectionUrl { get; set; }

        internal bool Restart { get; set; }
    }
}
