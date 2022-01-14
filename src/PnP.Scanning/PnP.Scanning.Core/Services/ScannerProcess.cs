using Microsoft.Extensions.Hosting;

namespace PnP.Scanning.Core.Services
{
    internal sealed class ScannerProcess
    {
        internal ScannerProcess(long processId, int port)
        {
            ProcessId = processId;
            Port = port;
        }

        internal long ProcessId { get; private set; }

        internal int Port { get; private set; }
    }
}
