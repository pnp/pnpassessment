using Grpc.Core;
using PnP.Scanning.Core;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Process.Services;
using Spectre.Console;
using System.CommandLine;
using System.ComponentModel;
using System.Runtime.InteropServices;

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
        private Option<bool> openGeneratedReportOption;

        internal ReportCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;

            cmd = new Command("report", "Generates the Microsoft 365 Assessment reports");

            scanIdOption = new(
                name: $"--{Constants.PauseScanId}",
                description: "Id of the Microsoft 365 Assessment to generate the report for")
            {
                IsRequired = false,
            };
            cmd.AddOption(scanIdOption);

            modeOption = new(
                name: $"--{Constants.ReportMode}",
                getDefaultValue: () => ReportMode.PowerBI,
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

            openGeneratedReportOption = new(
                name: $"--{Constants.ReportOpen}",
                getDefaultValue: () => true,
                description: "Open the generated report")
            {
                IsRequired = false,
            };
            cmd.AddOption(openGeneratedReportOption);
        }

        public Command Create()
        {
            // Custom validation of provided command input, use to validate option combinations
            //cmd.AddValidator(commandResult =>
            //{                               
            //});

            cmd.SetHandler(async (Guid scanId, ReportMode mode, Delimiter delimiter, string path, bool open) =>
                            {
                                await HandleStartAsync(scanId, mode, delimiter, path, open);
                            },
                            scanIdOption, modeOption, delimiterOption, exportPathOption, openGeneratedReportOption);

            return cmd;
        }

        private async Task HandleStartAsync(Guid scanId, ReportMode mode, Delimiter delimiter, string path, bool open)
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

                string finalReportPath = "";

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

                    if (!string.IsNullOrEmpty(message.ReportPath))
                    {
                        finalReportPath = message.ReportPath;
                    }
                }

                AnsiConsole.WriteLine();

                if (!string.IsNullOrEmpty(finalReportPath) && open)
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        if (mode == ReportMode.PowerBI)
                        {
                            // Open the PowerBI Desktop client
                            var powerBiDesktop = PowerBiManager.LaunchPowerBiAsync(finalReportPath);
                        }
                        else
                        {
                            // Open Windows explorer
                            try
                            {
                                _ = System.Diagnostics.Process.Start("explorer.exe", string.Format("\"{0}\"", finalReportPath));
                            }
                            catch (Win32Exception win32Exception)
                            {
                                AnsiConsole.WriteException(win32Exception);
                            }
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[green]Report output is available in folder {Path.GetDirectoryName(finalReportPath)}[/]");
                    }
                }

            });
        }
    }
}
