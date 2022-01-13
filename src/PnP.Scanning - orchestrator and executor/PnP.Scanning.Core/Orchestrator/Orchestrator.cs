using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using PnP.Scanning.Core.Services;
using Scanner;

namespace PnP.Scanning.Core.Orchestrator
{
    internal sealed class Orchestrator: OrchestratorService.OrchestratorServiceBase
    {
        private readonly ILogger logger;
        private ProcessManager processManager;

        public Orchestrator(ILoggerFactory loggerFactory, ProcessManager processManagerInstance)
        {
            logger = loggerFactory.CreateLogger<Orchestrator>();
            processManager = processManagerInstance;
        }

        public override async Task<StatusReply> Status(StatusRequest request, ServerCallContext context)
        {
            logger.LogInformation($"Status {request.Message} received");
            return new StatusReply() { Success = true };
        }

        public override async Task<OrchestratorStartReply> Start(OrchestratorStartRequest request, ServerCallContext context)
        {
            logger.LogWarning($"Orchestrator start requested for mode {request.Mode}");

            // Start executor process            
            var executorPort = processManager.LaunchExecutor("Workflow");

            // Get grpc client to communicate with executor
            var executorClient = new ExecutorService.ExecutorServiceClient(GrpcChannel.ForAddress($"http://localhost:{executorPort}"));

            // Kick off 3 parallel tasks on the executor service
            _ = Task.Run(async () => { await executorClient.StartAsync(new StartRequest() { Mode = "Workflow2013_0" }); });
            _ = Task.Run(async () => { await executorClient.StartAsync(new StartRequest() { Mode = "Workflow2013_1" }); });
            _ = Task.Run(async () => { await executorClient.StartAsync(new StartRequest() { Mode = "Workflow2013_2" }); });

            return new OrchestratorStartReply() { Success = true };
            //return base.Start(request, context);
        }
    }
}
