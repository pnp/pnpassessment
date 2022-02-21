using PnP.Core.Services;
using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Queues
{
    internal abstract class QueueItemBase
    {
        internal QueueItemBase(OptionsBase optionsBase, IPnPContextFactory pnPContextFactory, CsomEventHub csomEventHub)
        {
            OptionsBase = optionsBase;
            PnPContextFactory = pnPContextFactory;
            CsomEventHub = csomEventHub;
        }

        internal OptionsBase OptionsBase { get; set; }

        internal IPnPContextFactory PnPContextFactory { get; set; }

        internal CsomEventHub CsomEventHub { get; set; }
    }
}
