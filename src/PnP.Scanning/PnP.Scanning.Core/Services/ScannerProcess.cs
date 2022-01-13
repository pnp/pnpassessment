using Microsoft.Extensions.Hosting;

namespace PnP.Scanning.Core.Services
{
    internal sealed class ScannerProcess
    {
        internal ScannerProcess(long processId, int port, IHost kestrelWebServer, string scope)
        {
            ProcessId = processId;
            Port = port;
            KestrelWebServer = kestrelWebServer;
            Scope = scope;
        }

        internal long ProcessId { get; private set; }

        internal int Port { get; private set; }

        internal IHost KestrelWebServer { get; private set; }

        internal string Scope { get; private set; }
    }
}
