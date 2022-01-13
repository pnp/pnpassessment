using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace PnP.Scanning.Core.Services
{
    internal sealed class ProcessManager
    {
        public const int DefaultScannerPort = 25010;

        private readonly List<ScannerProcess> scannerProcesses = new();

        private readonly ILogger logger;

        private IHost kestrelWebServer;

        public ProcessManager(ILoggerFactory loggerFactory, IHost host)
        {
            logger = loggerFactory.CreateLogger<ProcessManager>();
            kestrelWebServer = host;
        }

        public int LaunchScannerProcess(string scope)
        {
            int port = GetFreeScannerPort();

            ProcessStartInfo startInfo = new()
            {
                FileName = "PnP.Scanning.Process.exe",
                Arguments = $"scanner {port}",
                UseShellExecute = true
#if !DEBUG
                ,WindowStyle = ProcessWindowStyle.Hidden
#endif
            };

            Process? scannerProcess = Process.Start(startInfo);

            if (scannerProcess != null && !scannerProcess.HasExited)
            {

                scannerProcesses.Add(new ScannerProcess(scannerProcess.Id, port, kestrelWebServer, scope));

#if DEBUG
                AttachDebugger(scannerProcess);
#endif

                return port;
            }
            else
            {
                return -1;
            }
        }

        public void RegisterScannerProcessForCli(long processId, int port, IHost kestrelWebServer)
        {
            scannerProcesses.Add(new ScannerProcess(processId, port, kestrelWebServer, ""));
        }

        private int GetFreeScannerPort()
        {
            if (!scannerProcesses.Any())
            {
                return DefaultScannerPort;
            }
            else
            {
                return GetAvailablePort(DefaultScannerPort);
            }
        }

        public ScannerProcess GetRunningScanner()
        {
            return scannerProcesses.First();
        }

        /// <summary>
        /// checks for used ports and retrieves the first free port
        /// </summary>
        /// <returns>the free port or 0 if it did not find a free port</returns>
        public static int GetAvailablePort(int startingPort)
        {
            IPEndPoint[] endPoints;
            List<int> portArray = new();

            IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();

            //getting active connections
            TcpConnectionInformation[] connections = properties.GetActiveTcpConnections();
            portArray.AddRange(from n in connections
                               where n.LocalEndPoint.Port >= startingPort
                               select n.LocalEndPoint.Port);

            //getting active tcp listners
            endPoints = properties.GetActiveTcpListeners();
            portArray.AddRange(from n in endPoints
                               where n.Port >= startingPort
                               select n.Port);

            //getting active udp listeners
            endPoints = properties.GetActiveUdpListeners();
            portArray.AddRange(from n in endPoints
                               where n.Port >= startingPort
                               select n.Port);

            portArray.Sort();

            for (int i = startingPort; i < ushort.MaxValue; i++)
            {
                if (!portArray.Contains(i))
                {
                    return i;
                }
            }

            return 0;
        }

#if DEBUG
        internal static void AttachDebugger(Process processToAttachTo)
        {
            var vsProcess = VisualStudioAttacher.GetVisualStudioForSolutions(new List<string> { "PnP.Scanning.sln" });

            if (vsProcess != null)
            {
                VisualStudioAttacher.AttachVisualStudioToProcess(vsProcess, processToAttachTo /*Process.GetCurrentProcess()*/);
            }
            else
            {
                // try and attach the old fashioned way
                Debugger.Launch();
            }

            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (Debugger.IsAttached)
            {

            }
        }
#endif
    }
}
