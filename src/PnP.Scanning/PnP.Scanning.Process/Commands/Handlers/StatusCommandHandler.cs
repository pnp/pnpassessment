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

            // Configure options for status

            cmd.SetHandler(async () => await HandleStatusAsync());

            return cmd;
        }

        private async Task HandleStatusAsync()
        {
            var status = await (await processManager.GetScannerClientAsync()).StatusAsync(new StatusRequest() { Message = "bla" });

            if (status.Status.Count == 0)
            {
                Console.WriteLine("There are no running scans anymore!");
            }
            else
            {
                Console.WriteLine($"There are {status.Status.Count} scans still running:");
                foreach(var statusMesage in status.Status)
                {
                    double procentDone = (double)statusMesage.SiteCollectionsScanned / statusMesage.SiteCollectionsToScan * 100;
                    Console.WriteLine($"Scan ({statusMesage.Id}) is {statusMesage.Status}, {statusMesage.SiteCollectionsScanned}/{statusMesage.SiteCollectionsToScan} ({procentDone}%) site collections done");
                }
            }
        }
    }
}
