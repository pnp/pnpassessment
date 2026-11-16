using PnP.Scanning.Core;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Process.Services;
using Spectre.Console;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    /// <summary>
    /// Binds existing list filters and renders assessment status without changing scan selection.
    /// </summary>
    internal sealed class ListCommandHandler
    {
        private readonly ScannerManager processManager;

        private Command cmd;

        private Option<bool> runningOption;
        private Option<bool> pausedOption;
        private Option<bool> finishedOption;
        private Option<bool> terminatedOption;

        internal ListCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;

            cmd = new Command("list", "Lists all the Microsoft 365 Assessments with their status");

            // Scanner mode
            runningOption = new(
                name: $"--{Constants.ListRunning}",
                getDefaultValue: () => false,
                description: "Lists running Microsoft 365 Assessments") { IsRequired = false, };
            cmd.AddOption(runningOption);

            pausedOption = new(
                name: $"--{Constants.ListPaused}",
                getDefaultValue: () => false,
                description: "Lists paused Microsoft 365 Assessments") { IsRequired = false, };
            cmd.AddOption(pausedOption);

            finishedOption = new(
                name: $"--{Constants.ListFinished}",
                getDefaultValue: () => false,
                description: "Lists finished Microsoft 365 Assessments") { IsRequired = false, };
            cmd.AddOption(finishedOption);

            terminatedOption = new(
                name: $"--{Constants.ListTerminated}",
                getDefaultValue: () => false,
                description: "Lists terminated Microsoft 365 Assessments") { IsRequired = false, };
            cmd.AddOption(terminatedOption);
        }

        public Command Create()
        {
            cmd.SetHandler(
                async (bool running, bool paused, bool finished, bool terminated) =>
                {
                    await HandleStartAsync(running, paused, finished, terminated);
                },
                runningOption,
                pausedOption,
                finishedOption,
                terminatedOption);

            return cmd;
        }

        /// <summary>
        /// Calls the existing list RPC and renders each assessment's status, stage, progress and issues.
        /// </summary>
        private async Task HandleStartAsync(bool running, bool paused, bool finished, bool terminated)
        {
            // Setup client to talk to scanner
            Core.Services.PnPScanner.PnPScannerClient client = await processManager.GetScannerClientAsync();

            Core.Services.ListReply listResult = await client.ListAsync(
                new Core.Services.ListRequest
                {
                    Running = running, Paused = paused, Finished = finished, Terminated = terminated,
                });

            if (listResult.Status.Count > 0)
            {
                // Create a table
                Table table = new Table().BorderColor(Color.Grey);

                // Add some columns
                table.AddColumn("Id ");
                table.AddColumn("Mode");
                table.AddColumn(new TableColumn("Status").Centered());
                table.AddColumn("Stage");
                table.AddColumn(new TableColumn("Progress").Centered());
                table.AddColumn("Started at");
                table.AddColumn("Ended at");
                table.AddColumn("Issues");

                foreach (Core.Services.ListScanResponse item in listResult.Status)
                {
                    double procentDone = Math.Round(
                        (double)item.SiteCollectionsScanned / item.SiteCollectionsToScan * 100);
                    Markup status;
                    Markup procent;
                    if (item.Status == ScanStatus.Running.ToString())
                    {
                        status = new Markup($"[orange3]{item.Status}[/]");
                        procent = new Markup(
                            $"[orange3]{item.SiteCollectionsScanned}[/]/[green]{item.SiteCollectionsToScan}[/] ([orange3]{procentDone}%[/])");
                    }
                    else if (item.Status == ScanStatus.Finished.ToString())
                    {
                        status = new Markup($"[green]{item.Status}[/]");
                        procent = new Markup(
                            $"[green]{item.SiteCollectionsScanned}[/]/[green]{item.SiteCollectionsToScan}[/] ([green]{procentDone}%[/])");
                    }
                    else if (item.Status == ScanStatus.Terminated.ToString())
                    {
                        status = new Markup($"[maroon]{item.Status}[/]");
                        procent = new Markup(
                            $"[maroon]{item.SiteCollectionsScanned}[/]/[green]{item.SiteCollectionsToScan}[/] ([maroon]{procentDone}%[/])");
                    }
                    else if (item.Status == ScanStatus.Paused.ToString())
                    {
                        status = new Markup($"[grey50]{item.Status}[/]");
                        procent = new Markup(
                            $"[grey50]{item.SiteCollectionsScanned}[/]/[green]{item.SiteCollectionsToScan}[/] ([grey50]{procentDone}%[/])");
                    }
                    else if (item.Status == ScanStatus.Pausing.ToString())
                    {
                        status = new Markup($"[grey70]{item.Status}[/]");
                        procent = new Markup(
                            $"[grey70]{item.SiteCollectionsScanned}[/]/[green]{item.SiteCollectionsToScan}[/] ([grey70]{procentDone}%[/])");
                    }
                    else
                    {
                        status = new Markup($"{item.Status}");
                        procent = new Markup(
                            $"{item.SiteCollectionsScanned}/{item.SiteCollectionsToScan} ({procentDone}%)");
                    }

                    if (item.ExecutionPath == "pipeline")
                        procent = new Markup(
                            Markup.Escape(
                                AssessmentProgressText.Progress(
                                    item.ProgressCompleted,
                                    item.ProgressTotal,
                                    item.ProgressUnit)));

                    Markup endedAt;
                    if (item.ScanEnded.ToDateTime() == DateTime.MinValue)
                    {
                        endedAt = new Markup("");
                    }
                    else
                    {
                        endedAt = new Markup($"{item.ScanEnded.ToDateTime().ToLocalTime()}");
                    }

                    table.AddRow(
                        new Markup($"{item.Id}"),
                        new Markup($"{item.Mode}"),
                        status,
                        new Markup(Markup.Escape(AssessmentProgressText.Stage(item.Stage, item.Status))),
                        procent,
                        new Markup($"{item.ScanStarted.ToDateTime().ToLocalTime()}"),
                        endedAt,
                        new Markup(
                            Markup.Escape(
                                AssessmentProgressText.Issues(
                                    item.Status,
                                    item.ReadIssues,
                                    item.AnalysisIssues,
                                    item.ErrorSummary))));
                }

                AnsiConsole.Write(table);
            }
            else
            {
                AnsiConsole.WriteLine("No Microsoft 365 Assessments found meeting the set criteria...");
            }
        }
    }
}