using PnP.Core.Services;
using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Queues
{
    internal class SiteCollectionQueueItem : QueueItemBase
    {
        internal SiteCollectionQueueItem(OptionsBase optionsBase, IPnPContextFactory pnPContextFactory, CsomEventHub csomEventHub, string siteCollectionUrl) : 
            base(optionsBase, pnPContextFactory, csomEventHub)
        {
            SiteCollectionUrl = siteCollectionUrl;
        }

        internal string SiteCollectionUrl { get; set; }

        internal bool Restart { get; set; }
    }
}
