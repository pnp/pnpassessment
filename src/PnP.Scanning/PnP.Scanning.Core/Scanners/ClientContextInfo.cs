using Serilog;

namespace PnP.Scanning.Core.Scanners
{
    internal sealed class ClientContextInfo
    {
        internal ClientContextInfo(Guid scanId, CsomEventHub csomEventHub, ILogger logger)
        {
            ScanId = scanId;
            CsomEventHub = csomEventHub;
            Logger = logger;
        }

        internal Guid ScanId { get; private set; }

        internal CsomEventHub CsomEventHub { get; private set; }

        internal ILogger Logger { get; private set; }
    }
}
