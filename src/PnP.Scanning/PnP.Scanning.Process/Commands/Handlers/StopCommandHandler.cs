using PnP.Scanning.Core.Services;
using PnP.Scanning.Process.Services;
using Spectre.Console;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class StopCommandHandler
    {
        private readonly ScannerManager processManager;

        public StopCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;
        }

        public Command Create()
        {
            var cmd = new Command("stop", "Stops the scan engine, terminating all running scans");

            // Configure options for stop

            cmd.SetHandler(async () => await HandleStopAsync());

            return cmd;
        }

        private async Task HandleStopAsync()
        {
            try
            {
                AnsiConsole.MarkupLine("[gray]Requesting the scanner server to shutdown...[/]");
                await (await processManager.GetScannerClientAsync()).StopAsync(new StopRequest() { Site = "" });
            }
            catch
            {
                //Eat all exceptions that might come from stopping the scanner
            }
        }
    }
}
