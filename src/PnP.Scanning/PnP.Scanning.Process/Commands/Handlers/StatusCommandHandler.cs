using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Process.Services;
using Spectre.Console;
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
            var cmd = new Command("status", "Realtime status of the currently running Microsoft 365 Assessments");

            // Configure options for status

            cmd.SetHandler(async () => await HandleStatusAsync());

            return cmd;
        }

        private async Task HandleStatusAsync()
        {
            var client = await processManager.GetScannerClientAsync();

            var table = new Table().BorderColor(Color.Grey);

            // Add some columns
            table.AddColumn(new TableColumn("Id").Centered());
            table.AddColumn(new TableColumn("Mode").Centered());
            table.AddColumn(new TableColumn("Status").Centered());
            table.AddColumn(new TableColumn("Progress").Centered());
            table.AddColumn(new TableColumn("Retries").Centered());
            table.AddColumn(new TableColumn("Session start").Centered());
            table.AddColumn(new TableColumn("Session duration").Centered());

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Live running Microsoft 365 Assessment status. Press [yellow]ESC[/] to exit");
            AnsiConsole.WriteLine();

            await AnsiConsole.Live(table)
                .AutoClear(false)   // Do not remove when done
                .Overflow(VerticalOverflow.Ellipsis) // Show ellipsis when overflowing
                .Cropping(VerticalOverflowCropping.Top) // Crop overflow at top
                .StartAsync(async ctx =>
                {
                    do
                    {
                        while (!Console.KeyAvailable)
                        {
                            var statusInformation = await client.StatusAsync(new StatusRequest() { Message = "bla" });

                            table.Rows.Clear();

                            if (statusInformation.Status.Count > 0)
                            {
                                foreach (var item in statusInformation.Status)
                                {
                                    double procentDone = Math.Round((double)item.SiteCollectionsScanned / item.SiteCollectionsToScan * 100);
                                    Markup status;
                                    Markup procent;
                                    Markup throttling = null;
                                    if (item.Status == ScanStatus.Running.ToString() || item.Status == "Finalizing")
                                    {
                                        status = new Markup($"[orange3]{item.Status}[/]");
                                        procent = new Markup($"[orange3]{item.SiteCollectionsScanned}[/]/[green]{item.SiteCollectionsToScan}[/] ([orange3]{procentDone}%[/])");
                                    }
                                    else if (item.Status == ScanStatus.Finished.ToString())
                                    {
                                        status = new Markup($"[green]{item.Status}[/]");
                                        procent = new Markup($"[green]{item.SiteCollectionsScanned}[/]/[green]{item.SiteCollectionsToScan}[/] ([green]{procentDone}%[/])");
                                    }
                                    else if (item.Status == ScanStatus.Terminated.ToString())
                                    {
                                        status = new Markup($"[maroon]{item.Status}[/]");
                                        procent = new Markup($"[maroon]{item.SiteCollectionsScanned}[/]/[green]{item.SiteCollectionsToScan}[/] ([maroon]{procentDone}%[/])");
                                    }
                                    else
                                    {
                                        status = new Markup($"{item.Status}");
                                        procent = new Markup($"{item.SiteCollectionsScanned}/{item.SiteCollectionsToScan} ({procentDone}%)");
                                    }

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
                                    throttling = new Markup($"{item.RequestsThrottled} / {item.RequestsRetriedDueToNetworkError}");
                                    //}

                                    table.AddRow(new Markup($"{item.Id}"),
                                                 new Markup($"{item.Mode}"),
                                                 status,
                                                 procent,
                                                 throttling,
                                                 new Markup($"{item.Started.ToDateTime().ToLocalTime()}"),
                                                 new Markup(item.Duration.ToTimeSpan().ToString(@"dd\:hh\:mm\:ss")));
                                }
                            }
                            else
                            {
                                table.AddRow(new Markup($"[green]No running scans[/]"));
                            }

                            ctx.Refresh();

                            await Task.Delay(TimeSpan.FromSeconds(1));
                        }
                    }
                    while (Console.ReadKey(true).Key != ConsoleKey.Escape);
                });

        }
    }
}
