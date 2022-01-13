using Grpc.Core;
using Microsoft.Extensions.Logging;
using Scanner;

namespace PnP.Scanning.Core.Executor
{
    internal sealed class Executor : ExecutorService.ExecutorServiceBase
    {
        private readonly ILogger logger;
        private readonly OrchestratorService.OrchestratorServiceClient orchestratorClient;

        public Executor(ILoggerFactory loggerFactory, OrchestratorService.OrchestratorServiceClient orchestrator)
        {
            logger = loggerFactory.CreateLogger<Executor>();
            orchestratorClient = orchestrator;
        }

        public override async Task<InitReply> Init(InitRequest request, ServerCallContext context)
        {
            logger.LogInformation($"Init request for database {request.DbName} received");
            //return base.Init(request, context);
            return new InitReply() { Success = true, Error = "" };
        }

        public override async Task<StartReply> Start(StartRequest request, ServerCallContext context)
        {
            try
            {

                logger.LogWarning($"Started for {request.Mode} ThreadId : {Thread.CurrentThread.ManagedThreadId}");

                int delay = new Random().Next(500, 10000);
                await Task.Delay(delay);

                logger.LogWarning($"Step 1 Delay {delay} ThreadId : {Thread.CurrentThread.ManagedThreadId}");
                await orchestratorClient.StatusAsync(new StatusRequest() { Message = $"Step 1 - Executor ThreadId : {Thread.CurrentThread.ManagedThreadId}" });

                delay = new Random().Next(500, 10000);
                await Task.Delay(delay);

                logger.LogWarning($"Step 2 Delay {delay} ThreadId : {Thread.CurrentThread.ManagedThreadId}");
                await orchestratorClient.StatusAsync(new StatusRequest() { Message = $"Step 2 - Executor ThreadId : {Thread.CurrentThread.ManagedThreadId}" });

                delay = new Random().Next(500, 10000);
                await Task.Delay(delay);

                logger.LogWarning($"Step 3 Delay {delay} ThreadId : {Thread.CurrentThread.ManagedThreadId}");
                await orchestratorClient.StatusAsync(new StatusRequest() { Message = $"Step 3 - Executor ThreadId : {Thread.CurrentThread.ManagedThreadId}" });
            }
            catch (Exception ex)
            {
                //await orchestratorClient.StatusAsync(new StatusRequest() { Message = ex.ToString() });
                return new StartReply() { Success = false };
            }

            //return base.Start(request, context);
            return new StartReply() { Success = true };
        }

    }
}
