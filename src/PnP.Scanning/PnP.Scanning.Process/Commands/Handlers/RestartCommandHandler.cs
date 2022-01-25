using Grpc.Core;
using PnP.Scanning.Core;
using PnP.Scanning.Process.Services;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal sealed class RestartCommandHandler
    {
        private readonly ScannerManager processManager;

        private Command cmd;

        private Option<Guid> scanIdOption;

        internal RestartCommandHandler(ScannerManager processManagerInstance)
        {
            processManager = processManagerInstance;

            cmd = new Command("restart", "Restarts a paused or terminated scan");

            scanIdOption = new(
                name: $"--{Constants.PauseScanId}",
                description: "Id of the scan to restart")
            {
                IsRequired = true,
            };
            cmd.AddOption(scanIdOption);
        }

        public Command Create()
        {
            cmd.SetHandler(async (Guid scanId) => 
                            { 
                                await HandleRestartAsync(scanId); 
                            },
                            scanIdOption);

            return cmd;
        }

        private async Task HandleRestartAsync(Guid scanId)
        {
            // Setup client to talk to scanner
            var client = await processManager.GetScannerClientAsync();

            // Restart the scan
            var call = client.Restart(new Core.Services.RestartRequest {  Id = scanId.ToString() });
            await foreach (var message in call.ResponseStream.ReadAllAsync())
            {
                ColorConsole.WriteInfo($"Status: {message.Status}");
            }
        }
    }
}
