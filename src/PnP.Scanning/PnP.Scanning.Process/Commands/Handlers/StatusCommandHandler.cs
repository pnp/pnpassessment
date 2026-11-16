using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Process.Services;
using Spectre.Console;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    /// <summary>
    /// Displays the status, stage, progress and issues of running assessments through the existing command.
    /// </summary>
    internal sealed class StatusCommandHandler
    {
        private readonly ScannerManager processManager;

        public StatusCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;
        }

        public Command Create()
        {
            var cmd = new Command("status", "Realtime status of the currently running Microsoft 365 Assessments");

            // Configure options for status

            cmd.SetHandler(async () => await HandleStatusAsync());

            return cmd;
        }

        /// <summary>
        /// Reads and displays each running assessment's stage, progress and issues, refreshed every second.
        /// </summary>
        private async Task HandleStatusAsync()
        {
            PnPScanner.PnPScannerClient client = await processManager.GetScannerClientAsync();

            Table table = new Table().BorderColor(Color.Grey);

            // Add some columns
            table.AddColumn(new TableColumn("Id").Centered());
            table.AddColumn(new TableColumn("Mode").Centered());
            table.AddColumn(new TableColumn("Status").Centered());
            table.AddColumn("Stage");
            table.AddColumn(new TableColumn("Progress").Centered());
            table.AddColumn(new TableColumn("Retries").Centered());
            table.AddColumn(new TableColumn("Session start").Centered());
            table.AddColumn(new TableColumn("Session duration").Centered());
            table.AddColumn("Issues");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Live running Microsoft 365 Assessment status. Press [yellow]ESC[/] to exit");
            AnsiConsole.WriteLine();

            await AnsiConsole.Live(table)
                .AutoClear(false) // Do not remove when done
                .Overflow(VerticalOverflow.Ellipsis) // Show ellipsis when overflowing
                .Cropping(VerticalOverflowCropping.Top) // Crop overflow at top
                .StartAsync(async ctx =>
                {
                    do
                    {
                        while (!Console.KeyAvailable)
                        {
                            StatusReply statusInformation =
                                await client.StatusAsync(new StatusRequest() { Message = "bla" });

                            table.Rows.Clear();

                            if (statusInformation.Status.Count > 0)
                            {
                                foreach (ScanStatusReply item in statusInformation.Status)
                                {
                                    double procentDone = Math.Round(
                                        (double)item.SiteCollectionsScanned / item.SiteCollectionsToScan * 100);
                                    Markup status;
                                    Markup procent;
                                    Markup throttling = null;
                                    if (item.Status == ScanStatus.Running.ToString() || item.Status == "Finalizing")
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
                                    else
                                    {
                                        status = new Markup($"{item.Status}");
                                        procent = new Markup(
                                            $"{item.SiteCollectionsScanned}/{item.SiteCollectionsToScan} ({procentDone}%)");
                                    }

                                    bool pipeline = item.ExecutionPath == "pipeline";
                                    if (pipeline)
                                        procent = new Markup(
                                            Markup.Escape(
                                                AssessmentProgressText.Progress(
                                                    item.ProgressCompleted,
                                                    item.ProgressTotal,
                                                    item.ProgressUnit)));

                                    //if (item.RetryingRequestAt.ToDateTime() != DateTime.MinValue)
                                    //{
                                    //    TimeSpan retryAfter = DateTime.Now - item.RetryingRequestAt.ToDateTime().ToLocalTime();
                                    //    if (retryAfter.Seconds > 0)
                                    //    {
                                    //        throttling = new Markup($"{item.RequestsThrottled} / {item.RequestsRetriedDueToNetworkError} / {retryAfter.Seconds} sec");
                                    //    }
                                    //}

                                    //if (throttling == null)
                                    //{
                                    // Retry counts are reported for site-based assessments only; a dash marks the rest.
                                    throttling = pipeline
                                        ? new Markup("-")
                                        : new Markup(
                                            $"{item.RequestsThrottled} / {item.RequestsRetriedDueToNetworkError}");
                                    //}

                                    table.AddRow(
                                        new Markup($"{item.Id}"),
                                        new Markup($"{item.Mode}"),
                                        status,
                                        new Markup(
                                            Markup.Escape(AssessmentProgressText.Stage(item.Stage, item.Status))),
                                        procent,
                                        throttling,
                                        new Markup($"{item.Started.ToDateTime().ToLocalTime()}"),
                                        new Markup(item.Duration.ToTimeSpan().ToString(@"dd\:hh\:mm\:ss")),
                                        new Markup(
                                            Markup.Escape(
                                                AssessmentProgressText.Issues(
                                                    item.Status,
                                                    item.ReadIssues,
                                                    item.AnalysisIssues,
                                                    item.ErrorSummary))));
                                }
                            }
                            else
                            {
                                table.AddRow(new Markup($"[green]No running scans[/]"));
                            }

                            ctx.Refresh();

                            await Task.Delay(TimeSpan.FromSeconds(1));
                        }
                    } while (Console.ReadKey(true).Key != ConsoleKey.Escape);
                });
        }
    }
}