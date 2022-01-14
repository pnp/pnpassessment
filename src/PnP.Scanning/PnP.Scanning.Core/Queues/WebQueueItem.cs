using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Queues
{
    internal class WebQueueItem : SiteCollectionQueueItem
    {
        internal WebQueueItem(OptionsBase optionsBase, string siteCollectionUrl, string webUrl) : base(optionsBase, siteCollectionUrl)
        {
            WebUrl = webUrl;
        }

        internal string WebUrl { get; set; }
    }
}
