using Grpc.Core;
using PnP.Scanning.Core;
using PnP.Scanning.Process.Services;
using Spectre.Console;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class PauseCommandHandler
    {
        private readonly ScannerManager processManager;

        private Command cmd;

        private Option<Guid> scanIdOption;
        private Option<bool> allScansOption;

        internal PauseCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;

            cmd = new Command("pause", "Pauses one or all Microsoft 365 Assessments");

            scanIdOption = new(
                name: $"--{Constants.PauseScanId}",
                description: "Id of the Microsoft 365 Assessment to pause")
            {
                IsRequired = false,
            };
            cmd.AddOption(scanIdOption);

            allScansOption = new(
                name: $"--{Constants.PauseAll}",
                description: "Pause all Microsoft 365 Assessments")
            {
                IsRequired = false,
            };
            allScansOption.SetDefaultValue(false);

            cmd.AddOption(allScansOption);
        }

        public Command Create()
        {
            // Custom validation of provided command input, use to validate option combinations
            cmd.AddValidator(commandResult =>
            {

                if (// no arguments
                    (commandResult.FindResultFor(scanIdOption) == null && commandResult.FindResultFor(allScansOption).GetValueOrDefault<bool>() == false) ||
                    // --scanid and --all
                    (commandResult.FindResultFor(scanIdOption) != null && commandResult.FindResultFor(allScansOption).GetValueOrDefault<bool>() == true)
                   )
                {
                    return $"You need to either use the --{Constants.PauseScanId} with a valid Microsoft 365 Assessment id or the --{Constants.PauseAll} option";
                }
                else
                {
                    return null;
                }
            });

            cmd.SetHandler(async (Guid scanId, bool all) =>
                            {
                                await HandleStartAsync(scanId, all);
                            },
                            scanIdOption, allScansOption);

            return cmd;
        }

        private async Task HandleStartAsync(Guid scanId, bool all)
        {
            await AnsiConsole.Status().Spinner(Spinner.Known.BouncingBar).StartAsync("Pausing Microsoft 365 Assessment...", async ctx =>
            {
                // Setup client to talk to scanner
                var client = await processManager.GetScannerClientAsync();

                // Start the pausing work
                var call = client.Pause(new Core.Services.PauseRequest { Id = scanId.ToString(), All = all });
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
