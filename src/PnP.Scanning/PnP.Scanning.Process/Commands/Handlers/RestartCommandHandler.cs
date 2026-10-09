using Grpc.Core;
using PnP.Scanning.Core;
using PnP.Scanning.Process.Services;
using Spectre.Console;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class RestartCommandHandler
    {
        private readonly ScannerManager processManager;
        private readonly ConfigurationOptions configurationOptions;

        private Command cmd;

        private Option<Guid> scanIdOption;
        private Option<int> threadsOption;
        private Option<Guid?> runIdOption;

        internal RestartCommandHandler(ScannerManager processManagerInstance, ConfigurationOptions configurationOptionsInstance)
        {
            processManager = processManagerInstance;
            configurationOptions = configurationOptionsInstance;

            cmd = new Command("restart", "Restarts a paused or terminated Microsoft 365 Assessment");

            scanIdOption = new(
                name: $"--{Constants.PauseScanId}",
                description: "Id of the Microsoft 365 Assessment to restart")
            {
                IsRequired = true,
            };
            cmd.AddOption(scanIdOption);

            threadsOption = new(
                name: $"--{Constants.StartThreads}",
                description: "Override number of threads set at Microsoft 365 Assessment start")
            {
                IsRequired = false
            };
            cmd.AddOption(threadsOption);
            runIdOption = new Option<Guid?>("--run-id", "Optional pipeline root run ID; defaults to the latest run for the assessment");
            cmd.AddOption(runIdOption);
        }

        public Command Create()
        {
            cmd.SetHandler(async context =>
            {
                context.ExitCode = await HandleRestartAsync(context.ParseResult.GetValueForOption(scanIdOption),
                    context.ParseResult.GetValueForOption(threadsOption), context.ParseResult.GetValueForOption(runIdOption));
            });

            return cmd;
        }

        private async Task<int> HandleRestartAsync(Guid scanId, int threads, Guid? runId)
        {
            int exitCode = 0;
            await AnsiConsole.Status().Spinner(Spinner.Known.BouncingBar).StartAsync("Restarting Microsoft 365 Assessment...", async ctx =>
            {
                // Setup client to talk to scanner
                var client = await processManager.GetScannerClientAsync();

                // Restart the scan
                var call = client.Restart(new Core.Services.RestartRequest
                {
                    Id = scanId.ToString(),
                    Threads = threads,
                    RunId = runId?.ToString() ?? "",
                    AdminCenterUrl = (configurationOptions != null && !string.IsNullOrEmpty(configurationOptions.AdminCenterUrl)) ? configurationOptions.AdminCenterUrl : "",
                    MySiteHostUrl = (configurationOptions != null && !string.IsNullOrEmpty(configurationOptions.MySiteHostUrl)) ? configurationOptions.MySiteHostUrl : "",
                });

                await foreach (var message in call.ResponseStream.ReadAllAsync())
                {
                    if (message.Type == Constants.MessageError)
                    {
                        exitCode = 1;
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
            return exitCode;
        }
    }
}
