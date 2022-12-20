using Serilog;

namespace PnP.Scanning.Core.Scanners
{
    internal sealed class ClientContextInfo
    {
        internal ClientContextInfo(Guid scanId, ILogger logger, CancellationToken cancellationToken)
        {
            ScanId = scanId;
            Logger = logger;
            CancellationToken = cancellationToken;
        }

        internal Guid ScanId { get; private set; }

        internal ILogger Logger { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }
    }
}
