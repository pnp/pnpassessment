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
            var cmd = new Command("stop", "Stops the Microsoft 365 Assessment engine, terminating all running assessments");
            var id = new Option<Guid?>("--id", "Terminate one assessment while keeping the engine running");
            cmd.AddOption(id);

            // Configure options for stop

            cmd.SetHandler(async context =>
            {
                var assessmentId = context.ParseResult.GetValueForOption(id);
                if (assessmentId == null) await HandleStopAsync();
                else
                {
                    try
                    {
                        var client = await processManager.GetScannerClientAsync();
                        if (!(await client.PingAsync(new Google.Protobuf.WellKnownTypes.Empty())).SupportsPipeline)
                        {
                            Console.Error.WriteLine("The running assessment service does not support stop --id. Restart it using this version of the tool.");
                            context.ExitCode = 1;
                            return;
                        }
                        await client.StopAsync(new StopRequest { Id = assessmentId.ToString() });
                        Console.WriteLine($"Assessment {assessmentId} terminated at its committed checkpoint.");
                    }
                    catch (Grpc.Core.RpcException ex)
                    {
                        Console.Error.WriteLine(ex.Status.Detail);
                        context.ExitCode = 1;
                    }
                }
            });

            return cmd;
        }

        private async Task HandleStopAsync()
        {
            try
            {
                AnsiConsole.MarkupLine("[gray]Requesting the Microsoft 365 Assessment process to shutdown...[/]");

                // Verify if a stop operation is really needed
                if (await processManager.IsScannerRunningAsync())
                {
                    // Connect to the scanner process
                    await (await processManager.GetScannerClientAsync()).StopAsync(new StopRequest() { Site = "" });

                    bool isGrpcUpAndRunning = true;
                    var retryAttempt = 1;
                    do
                    {
                        try
                        {
                            using (var process = System.Diagnostics.Process.GetProcessById(processManager.CurrentScannerProcessId))
                            {
                                if (process == null || process.HasExited)
                                {
                                    AnsiConsole.MarkupLine($"[green]OK[/]");
                                    isGrpcUpAndRunning = false;
                                }
                                else
                                {
                                    // Wait in between checks
                                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                                    retryAttempt++;
                                }
                            }
                        }
                        catch
                        {
                            AnsiConsole.MarkupLine($"[green]OK[/]");

                            // Exception means the process is not found
                            isGrpcUpAndRunning = false;
                        }
                    }
                    while (isGrpcUpAndRunning && retryAttempt <= 20);

                    if (isGrpcUpAndRunning)
                    {
                        AnsiConsole.MarkupLine($"[red]FAIL[/]");
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("[gray]The Microsoft 365 Assessment process was not running[/]");
                }
            }
            catch
            {
                //Eat all exceptions that might come from stopping the scanner
            }
        }
    }
}
