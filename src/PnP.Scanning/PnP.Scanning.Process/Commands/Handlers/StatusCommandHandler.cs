using PnP.Scanning.Core.Services;
using PnP.Scanning.Process.Services;
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
                ColorConsole.WriteLine("There are no running scans anymore!", ConsoleColor.Green);
            }
            else
            {
                ColorConsole.WriteEmbeddedColorLine($"There are [green]{status.Status.Count}[/green] scans still running:");
                ColorConsole.WriteLine("");
                ColorConsole.WriteLine(new string('-', ColorConsole.MaxWidth));
                ColorConsole.WriteLine($"Scan id".PadRight(36) + " | Site collection scan status");
                ColorConsole.WriteLine(new string('-', ColorConsole.MaxWidth));

                foreach (var statusMesage in status.Status)
                {
                    double procentDone = (double)statusMesage.SiteCollectionsScanned / statusMesage.SiteCollectionsToScan * 100;
                    ColorConsole.WriteEmbeddedColorLine(string.Format("{0} | [green]{1}[/green]/[green]{2}[/green] ([green]{3}%[/green]) site collections done", 
                        statusMesage.Id, statusMesage.SiteCollectionsScanned, statusMesage.SiteCollectionsToScan, procentDone));
                }
                ColorConsole.WriteLine(new string('-', ColorConsole.MaxWidth));
            }
        }
    }
}
