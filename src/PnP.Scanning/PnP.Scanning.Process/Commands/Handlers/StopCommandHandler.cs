using Grpc.Net.Client;
using PnP.Scanning.Core.Services;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class StopCommandHandler
    {
        private readonly ProcessManager processManager;

        public StopCommandHandler(ProcessManager processManagerInstance)
        {
            processManager = processManagerInstance;
        }

        public Command Create()
        {
            var cmd = new Command("stop", "stops all the scans");

            // Configure options for stop

            cmd.SetHandler(async () =>
            {
                // Get the port the scanner is running on
                var scannerPort = processManager.GetRunningScanner().Port;

                // Setup grpc client to the scanner
                var client = new PnPScanner.PnPScannerClient(GrpcChannel.ForAddress($"http://localhost:{scannerPort}"));

                try
                {
                    await client.StopAsync(new StopRequest() { Site = "bla" });
                }
                catch (Exception ex)
                {
                    //Console.WriteLine(ex.Message);
                }
            });

            return cmd;
        }
    }
}
