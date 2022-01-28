using Grpc.Core;
using PnP.Scanning.Core;
using PnP.Scanning.Process.Services;
using Spectre.Console;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class ReportCommandHandler
    {
        private readonly ScannerManager processManager;

        private Command cmd;

        private Option<Guid> scanIdOption;
        private Option<ReportMode> modeOption;
        private Option<Delimiter> delimiterOption;
        private Option<string> exportPathOption;

        internal ReportCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;

            cmd = new Command("report", "Generates the scan reports");

            scanIdOption = new(
                name: $"--{Constants.PauseScanId}",
                description: "Id of the scan to pause")
            {
                IsRequired = false,
            };
            cmd.AddOption(scanIdOption);

            modeOption = new(
                name: $"--{Constants.ReportMode}",
                getDefaultValue: () => ReportMode.PowerBi,
                description: "Select the report option")
            {
                IsRequired = true,
            };
            cmd.AddOption(modeOption);

            delimiterOption = new(
                name: $"--{Constants.ReportDelimiter}",
                getDefaultValue: () => Delimiter.Comma,
                description: "The delimiter to use for the CSV files")
            {
                IsRequired = false,
            };
            cmd.AddOption(delimiterOption);

            exportPathOption = new(
                name: $"--{Constants.ReportPath}",
                getDefaultValue: () => "",
                description: "The path to create the report in")
            {
                IsRequired = false,
            };
            cmd.AddOption(exportPathOption);
        }

        public Command Create()
        {
            // Custom validation of provided command input, use to validate option combinations
            //cmd.AddValidator(commandResult =>
            //{                               
            //});

            cmd.SetHandler(async (Guid scanId, ReportMode mode, Delimiter delimiter, string path) => 
                            { 
                                await HandleStartAsync(scanId, mode, delimiter, path); 
                            },
                            scanIdOption, modeOption, delimiterOption, exportPathOption);

            return cmd;
        }

        private async Task HandleStartAsync(Guid scanId, ReportMode mode, Delimiter delimiter, string path)
        {
            await AnsiConsole.Status().Spinner(Spinner.Known.BouncingBar).StartAsync("Creating reports...", async ctx =>
            {
                // Setup client to talk to scanner
                var client = await processManager.GetScannerClientAsync();

                string delimChar = ",";
                if (delimiter == Delimiter.Semicolon)
                {
                    delimChar = ";";
                }

                // Start the pausing work
                var call = client.Report(new Core.Services.ReportRequest 
                { 
                    Id = scanId.ToString(), 
                    Mode = mode.ToString(), 
                    Delimiter = delimChar,
                    Path = path
                });

                await foreach (var message in call.ResponseStream.ReadAllAsync())
                {
                    if (message.Type == Constants.MessageError)
                    {
                        AnsiConsole.MarkupLine($"[red]{message.Status}[/]");
                    }
                    else if (message.Type == Constants.MessageWarning)
                    {
                        AnsiConsole.MarkupLine($"[orange3]{message.Status}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[gray]{message.Status}[/]");
                    }

                    // Add delay for an improved "visual" experience
                    await Task.Delay(TimeSpan.FromMilliseconds(500));

                }
            });
        }
    }
}
