using PnP.Scanning.Core;
using PnP.Scanning.Process.Services;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class ListCommandHandler
    {
        private readonly ScannerManager processManager;

        private Command cmd;

        private Option<bool> runningOption;
        private Option<bool> pausedOption;
        private Option<bool> finishedOption;
        private Option<bool> failedOption;

        internal ListCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;

            cmd = new Command("list", "Lists the all the scans with their status");

            // Scanner mode
            runningOption = new(
                name: $"--{Constants.ListRunning}",
                getDefaultValue: () => false,
                description: "Lists running scans"
                )
            {
                IsRequired = false,
            };
            cmd.AddOption(runningOption);

            pausedOption = new(
                name: $"--{Constants.ListPaused}",
                getDefaultValue: () => false,
                description: "Lists paused scans"
                )
            {
                IsRequired = false,
            };
            cmd.AddOption(pausedOption);

            finishedOption = new(
                name: $"--{Constants.ListFinished}",
                getDefaultValue: () => false,
                description: "Lists finished scans"
                )
            {
                IsRequired = false,
            };
            cmd.AddOption(finishedOption);

            failedOption = new(
                name: $"--{Constants.ListFailed}",
                getDefaultValue: () => false,
                description: "Lists failed scans"
                )
            {
                IsRequired = false,
            };
            cmd.AddOption(failedOption);

        }

        public Command Create()
        {
            cmd.SetHandler(async (bool running, bool paused, bool finished, bool failed) => 
                            { 
                                await HandleStartAsync(running, paused, finished, failed); 
                            }, 
                            runningOption, pausedOption, finishedOption, failedOption);

            return cmd;
        }

        private async Task HandleStartAsync(bool running, bool paused, bool finished, bool failed)
        {
            // Setup client to talk to scanner
            var client = await processManager.GetScannerClientAsync();

            var listResult = await client.ListAsync(new Core.Services.ListRequest
            {
                Running = running,
                Paused = paused,
                Finished = finished,
                Failed = failed,
            });


            if (listResult.Status.Count > 0)
            {
                ColorConsole.WriteLine(new string('-', ColorConsole.MaxWidth));
                ColorConsole.WriteLine($"Scan id".PadRight(36) + " | Status " + " | Progress   " + " | Start           | Stop     ");
                ColorConsole.WriteLine(new string('-', ColorConsole.MaxWidth));

                foreach (var item in listResult.Status)
                {
                    double procentDone = Math.Round((double)item.SiteCollectionsScanned / item.SiteCollectionsToScan * 100);

                    ColorConsole.Write($"{item.Id} |");
                    ColorConsole.Write($"{item.Status} |".PadRight(10));
                    ColorConsole.Write($"{item.SiteCollectionsScanned}/{item.SiteCollectionsToScan} ({procentDone}%) |".PadRight(13));
                    ColorConsole.Write($"{item.ScanStarted.ToDateTime()} |".PadRight(11));
                    ColorConsole.WriteLine($"{item.ScanEnded.ToDateTime()} |".PadRight(11));
                }
            }
            else
            {
                ColorConsole.WriteInfo("No scans found meeting the set criteria");
            }
        }
    }
}
