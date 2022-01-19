using Grpc.Net.Client;
using PnP.Scanning.Core.Services;
using System.Diagnostics;

namespace PnP.Scanning.Process.Services
{
    /// <summary>
    /// Class responsible for getting a connection to an an up and running scanner process (= GRPC server)
    /// </summary>
    internal sealed class ScannerManager
    {
        public ScannerManager()
        {
        }

        internal static string DefaultScannerHost { get; } = "http://localhost";

        internal static int DefaultScannerPort { get; } = 25010;

        internal int CurrentScannerPort { get; private set; }           

        internal async Task<PnPScanner.PnPScannerClient> GetScannerClientAsync()
        {
            // Check if we previously connected with the scanner process
            if (CurrentScannerPort == 0)
            {
                // Launch the scanner
                await LaunchScannerAsync();
                // We can immediately return the client as during launch we ensured the server was up
                return CreateClient(CurrentScannerPort);
            }
            else
            {
                // We're aware of a previously connected scanner, let's see if we can connect to it
                var client = CreateClient(CurrentScannerPort);

                // Ensure the process is up and running via a ping
                try
                {
                    var response = await PingScannerAsync(client);
                    if (response != null && response.UpAndRunning)
                    {
                        return client;
                    }
                    else
                    {
                        throw new Exception("No scanner found");
                    }

                }
                catch
                {
                    // We did not find a scanner process, so launch the scanner again
                    if (await LaunchScannerAsync() > -1)
                    {
                        return CreateClient(CurrentScannerPort);
                    }
                    else
                    {
                        // Seems like we didn't manage to get the server up and running
                        throw;
                    }
                }
            }
        }

        private void RegisterScanner(int port)
        {
            CurrentScannerPort = port;
        }

        private async Task<int> LaunchScannerAsync()
        {
            int port = DefaultScannerPort;

            // First check if we can identify an existing running scanning process
            if (await CanConnectRunningScannerAsync(port))
            {
                RegisterScanner(port);
                return port;
            }
            else
            {
                ColorConsole.WriteWarning("No scanner found, starting one...");

                ProcessStartInfo startInfo = new()
                {
                    FileName = "PnP.Scanning.Process.exe",
                    Arguments = $"scanner {port}",
                    UseShellExecute = true
#if !DEBUG
                    ,WindowStyle = ProcessWindowStyle.Hidden
#endif
                };

                System.Diagnostics.Process? scannerProcess = System.Diagnostics.Process.Start(startInfo);

                if (scannerProcess != null && !scannerProcess.HasExited)
                {
                    RegisterScanner(port);

#if DEBUG
                    AttachDebugger(scannerProcess);
#endif

                    // perform a ping to verify when the grpc server is up
                    await WaitForScannerToBeUpAsync();

                    return port;
                }
                else
                {
                    return -1;
                }
            }
        }

        private async Task WaitForScannerToBeUpAsync()
        {
            var client = CreateClient(CurrentScannerPort);

            bool isGrpcUpAndRunning = false;
            var retryAttempt = 1;
            do
            {
                try
                {
                    // Wait in between pings
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                    retryAttempt++;

                    // Ping to see if the server is up
                    var response = await PingScannerAsync(client);
                    if (response != null)
                    {
                        isGrpcUpAndRunning = response.UpAndRunning;
                    }
                }
                catch 
                {
                }
            }
            while (!isGrpcUpAndRunning && retryAttempt <= 20);

            if (!isGrpcUpAndRunning)
            {
                throw new Exception("Scanner did not start timely");
            }
        }

        private static async Task<bool> CanConnectRunningScannerAsync(int port)
        {
            ColorConsole.WriteInfo($"Pinging scanner on port {port}...");

            var retryAttempt = 0;
            do
            {
                try
                {
                    var client = CreateClient(port);

                    // Ping to see if the server is up
                    var response = await PingScannerAsync(client);
                    if (response != null && response.UpAndRunning)
                    {
                        ColorConsole.WriteInfo($"Scanner detected on {port}");
                        return true;
                    }
                }
                catch
                {
                    retryAttempt++;

                    // Wait in between pings
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                }
            }
            while (retryAttempt <= 2);

            return false;
        }

        private static PnPScanner.PnPScannerClient CreateClient(int port)
        {
            return new PnPScanner.PnPScannerClient(GrpcChannel.ForAddress($"{DefaultScannerHost}:{port}"));
        }

        private async static Task<PingReply?> PingScannerAsync(PnPScanner.PnPScannerClient client)
        {
            // No point in waiting too long for a ping response
            return await client.PingAsync(new Google.Protobuf.WellKnownTypes.Empty(), deadline: DateTime.UtcNow.AddMilliseconds(500));
        }

        #region Not needed for now
        //private int GetFreeScannerPort()
        //{
        //    //if (!scannerProcesses.Any())
        //    //{
        //        return DefaultScannerPort;
        //    //}
        //    //else
        //    //{
        //    //    return GetAvailablePort(DefaultScannerPort);
        //    //}
        //}

        ///// <summary>
        ///// checks for used ports and retrieves the first free port
        ///// </summary>
        ///// <returns>the free port or 0 if it did not find a free port</returns>
        //private static int GetAvailablePort(int startingPort)
        //{
        //    IPEndPoint[] endPoints;
        //    List<int> portArray = new();

        //    IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();

        //    //getting active connections
        //    TcpConnectionInformation[] connections = properties.GetActiveTcpConnections();
        //    portArray.AddRange(from n in connections
        //                       where n.LocalEndPoint.Port >= startingPort
        //                       select n.LocalEndPoint.Port);

        //    //getting active tcp listners
        //    endPoints = properties.GetActiveTcpListeners();
        //    portArray.AddRange(from n in endPoints
        //                       where n.Port >= startingPort
        //                       select n.Port);

        //    //getting active udp listeners
        //    endPoints = properties.GetActiveUdpListeners();
        //    portArray.AddRange(from n in endPoints
        //                       where n.Port >= startingPort
        //                       select n.Port);

        //    portArray.Sort();

        //    for (int i = startingPort; i < ushort.MaxValue; i++)
        //    {
        //        if (!portArray.Contains(i))
        //        {
        //            return i;
        //        }
        //    }

        //    return 0;
        //}
        #endregion

#if DEBUG
        private static void AttachDebugger(System.Diagnostics.Process processToAttachTo)
        {
            var vsProcess = VisualStudioManager.GetVisualStudioForSolutions(new List<string> { "PnP.Scanning.sln" });

            if (vsProcess != null)
            {
                VisualStudioManager.AttachVisualStudioToProcess(vsProcess, processToAttachTo);
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
