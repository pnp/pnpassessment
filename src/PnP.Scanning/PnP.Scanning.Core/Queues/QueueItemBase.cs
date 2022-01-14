using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Queues
{
    internal abstract class QueueItemBase
    {
        internal QueueItemBase(OptionsBase optionsBase)
        {
            OptionsBase = optionsBase;
        }

        internal OptionsBase OptionsBase { get; set; }
    }
}
