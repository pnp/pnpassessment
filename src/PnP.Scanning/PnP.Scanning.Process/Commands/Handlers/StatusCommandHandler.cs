using PnP.Scanning.Core.Services;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class StatusCommandHandler
    {
        private readonly ScannerManager processManager;

        public StatusCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;
        }

        public Command Create()
        {
            var cmd = new Command("status", "status of the current scan");

            // Configure options for stop

            cmd.SetHandler(async () => await HandleStatusAsync());

            return cmd;
        }

        private async Task HandleStatusAsync()
        {
            var status = await (await processManager.GetScannerClientAsync()).StatusAsync(new StatusRequest() { Message = "bla" });
            if (status.AllSiteCollectionsProcessed)
            {
                Console.WriteLine("Scanner is done!");
            }
            else
            {
                Console.WriteLine($"Scanner is still running, {status.PendingSiteCollections} pending scanning");
            }            
        }
    }
}
