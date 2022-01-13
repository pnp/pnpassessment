using Grpc.Net.Client;
using PnP.Scanning.Core.Services;
using System.CommandLine;

namespace PnP.Scanning.Process.Commands
{
    internal class StartCommandHandler
    {
        private readonly ProcessManager processManager;

        public StartCommandHandler(ProcessManager processManagerInstance)
        {
            processManager = processManagerInstance;
        }

        /// <summary>
        /// https://github.com/dotnet/command-line-api/blob/main/docs/model-binding.md#more-complex-types
        /// </summary>
        /// <returns></returns>
        public Command Create()
        {
            var cmd = new Command("start", "starts a scan");

            // Configure options for start command

            var authenticationModeOption = new Option<AuthenticationMode>(
                    "--authmode",
                    getDefaultValue: () => AuthenticationMode.Interactive,
                    description: "Authentication mode used for the scan")
            {
                IsRequired = true
            };
            cmd.AddOption(authenticationModeOption);

            var certPathOption = new Option<string>(
                "--certpath",
                description: "Path to stored certificate in the form of StoreName|StoreLocation|Thumbprint. E.g. My|LocalMachine|3FG496B468BE3828E2359A8A6F092FB701C8CDB1")
            {
                IsRequired = false,
            };

            // Custom validation of the provided option input 
            certPathOption.AddValidator(val =>
            {
                string? input = val.GetValueOrDefault<string>();
                if (input != null && input.Split("|", StringSplitOptions.RemoveEmptyEntries).Length == 3)
                {
                    return null;
                }
                else
                {
                    return "Invalid certpath value";
                }
            });
            cmd.AddOption(certPathOption);


            var fileInfoOption = new Option<FileInfo>(
                "--certfile", "Path to certificate PFX file")
            {
                IsRequired = false
            };
            cmd.AddOption(fileInfoOption);

            // Custom validation of provided command input, use to validate option combinations
            cmd.AddValidator(val =>
            {

                return null;
            });

            var startBinder = new StartBinder(authenticationModeOption, certPathOption, fileInfoOption);
            cmd.SetHandler((StartOptions instance) => 
            { 
                Handle(instance);
            }, startBinder);            

            return cmd;
        }

        private void Handle(StartOptions arguments)
        {
            // Launch the orchestrator
            var orchestratorPort = processManager.LaunchOrchestrator("bla");

            // Give orchestrator some time to initialize
            // TODO: do some "ping" call to verify the grpc server is up
            Thread.Sleep(5000);

            // Setup grpc client to the orchestrator
            var orchestratorClient = new Scanner.OrchestratorService.OrchestratorServiceClient(GrpcChannel.ForAddress($"http://localhost:{orchestratorPort}"));

            // Kick off a scan
            var result = orchestratorClient.Start(new Scanner.OrchestratorStartRequest() { Mode = "Workflow" });
        }
    }
}
