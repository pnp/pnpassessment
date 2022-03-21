using Serilog;

namespace PnP.Scanning.Core.Scanners
{
    internal sealed class ClientContextInfo
    {
        internal ClientContextInfo(Guid scanId, CsomEventHub csomEventHub, ILogger logger, CancellationToken cancellationToken)
        {
            ScanId = scanId;
            CsomEventHub = csomEventHub;
            Logger = logger;
            CancellationToken = cancellationToken;
        }

        internal Guid ScanId { get; private set; }

        internal CsomEventHub CsomEventHub { get; private set; }

        internal ILogger Logger { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }
    }
}
