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

            cmd.SetHandler(async () => await HandleStopAsync());

            return cmd;
        }

        private async Task HandleStopAsync()
        {
            try
            {
                Console.WriteLine("Requesting the scanner server to shutdown...");
                await processManager.GetScannerClient().StopAsync(new StopRequest() { Site = "bla" });
            }
            catch
            {
                //Eat all exceptions that might come from stopping the scanner
            }
        }
    }
}
