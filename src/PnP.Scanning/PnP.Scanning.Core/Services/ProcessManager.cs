using Grpc.Net.Client;
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

        public ProcessManager(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<ProcessManager>();
        }

        internal async Task<int> LaunchScannerProcessAsync()
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
                RegisterScannerProcess(scannerProcess.Id, port);

#if DEBUG
                AttachDebugger(scannerProcess);
#endif

                // perform a ping to verify when the grpc server is up
                await WaitForGrpcServerToBeUpAsync(port, logger);

                return port;
            }
            else
            {
                return -1;
            }
        }

        internal void RegisterScannerProcess(long processId, int port)
        {
            scannerProcesses.Add(new ScannerProcess(processId, port));
        }

        private async Task WaitForGrpcServerToBeUpAsync(int port, ILogger logger)
        {
            // Setup grpc client to the scanner
            var client = GetScannerClient();

            bool isGrpcUpAndRunning = false;
            var retryAttempt = 1;
            do
            {
                try
                {
                    // Wait 1 second in between pings
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    
                    // Ping to see if the server is up
                    var response = await client.PingAsync(new Google.Protobuf.WellKnownTypes.Empty());
                    if (response != null)
                    {
                        isGrpcUpAndRunning = response.UpAndRunning;
                    }
                }
                catch (Exception ex)
                {
                    // Eat all exceptions
                    logger.LogWarning($"GRPC server ping: {ex.Message}");
                }
            }
            while (!isGrpcUpAndRunning && retryAttempt <= 10);

            if (!isGrpcUpAndRunning)
            {
                throw new Exception("Scanner server did not start timely");
            }
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

        internal PnPScanner.PnPScannerClient GetScannerClient()
        {
            if (scannerProcesses.Any())
            {
                return new PnPScanner.PnPScannerClient(GrpcChannel.ForAddress($"http://localhost:{scannerProcesses.First().Port}"));
            }
            else
            {
                throw new Exception("No scanner process was running");
            }
        }

        /// <summary>
        /// checks for used ports and retrieves the first free port
        /// </summary>
        /// <returns>the free port or 0 if it did not find a free port</returns>
        private static int GetAvailablePort(int startingPort)
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
        private static void AttachDebugger(Process processToAttachTo)
        {
            var vsProcess = VisualStudioAttacher.GetVisualStudioForSolutions(new List<string> { "PnP.Scanning.sln" });

            if (vsProcess != null)
            {
                VisualStudioAttacher.AttachVisualStudioToProcess(vsProcess, processToAttachTo);
            }
            else
            {
                // try and attach the old fashioned way
                Debugger.Launch();
            }

            if (Debugger.IsAttached)
            {
                // log something
            }
        }
#endif
    }
}
