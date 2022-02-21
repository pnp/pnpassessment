using PnP.Core.Services;
using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Queues
{
    internal class WebQueueItem : SiteCollectionQueueItem
    {
        internal WebQueueItem(OptionsBase optionsBase, IPnPContextFactory pnPContextFactory, CsomEventHub csomEventHub, string siteCollectionUrl, string webUrl) : 
            base(optionsBase, pnPContextFactory, csomEventHub, siteCollectionUrl)
        {
            WebUrl = webUrl;
        }

        internal string WebUrl { get; set; }
    }
}
