using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace PnP.Scanning.Core.Services
{
    internal sealed class Scanner: PnPScanner.PnPScannerBase
    {
        private readonly ILogger logger;
        private ProcessManager processManager;
        private PnPScanner.PnPScannerClient client;

        public Scanner(ILoggerFactory loggerFactory, ProcessManager processManagerInstance)
        {
            logger = loggerFactory.CreateLogger<Scanner>();
            processManager = processManagerInstance;
            var scannerPort = processManager.GetRunningScanner().Port;
            client = new PnPScanner.PnPScannerClient(GrpcChannel.ForAddress($"http://localhost:{scannerPort}"));
        }

        public override async Task<StatusReply> Status(StatusRequest request, ServerCallContext context)
        {
            logger.LogInformation($"Status {request.Message} received");
            return new StatusReply() { Success = true };
        }

        public override async Task<StopReply> Stop(StopRequest request, ServerCallContext context)
        {
            _ = Task.Run(async () => {
                await processManager.GetRunningScanner().KestrelWebServer.StopAsync();
            });
            return new StopReply() { Success = true };
        }

        public override async Task StartStreaming(StartRequest request, IServerStreamWriter<StartStatus> responseStream, ServerCallContext context)
        {
            var doneTcs = new TaskCompletionSource<bool>(); // bool is a dummy type

            await responseStream.WriteAsync(new StartStatus
            {                
                Status = "Starting"
            });

            for (int i = 0; i < 20; i++)
            {
                _ = Task.Run(async () =>
                {
                    var call = client.ScanSiteStreaming(new ScanSiteRequest() { Site = $"Site{i}" });
                    await foreach (var message in call.ResponseStream.ReadAllAsync())
                    {
                        Console.WriteLine($"Status: {message.Status}");
                    }
                });

                await responseStream.WriteAsync(new StartStatus
                {
                    Status = $"Site{i} scan launched"
                });
            }

            doneTcs.SetResult(true);

            await doneTcs.Task;
        }

        public override async Task<StartReply> Start(StartRequest request, ServerCallContext context)
        {
            logger.LogWarning($"Scanner start requested for mode {request.Mode}");

            //await client.ScanSiteAsync(new ScanSiteRequest() { Site = "Workflow2013_0" }, new CallOptions().WithWaitForReady(false));
            //await client.ScanSiteAsync(new ScanSiteRequest() { Site = "Workflow2013_1" }, new CallOptions().WithWaitForReady(false));
            //await client.ScanSiteAsync(new ScanSiteRequest() { Site = "Workflow2013_2" }, new CallOptions().WithWaitForReady(false));

            // Kick off 3 parallel tasks on the executor service
            _ = Task.Run(async () => { await client.ScanSiteAsync(new ScanSiteRequest() { Site = "Workflow2013_0" }); });
            _ = Task.Run(async () => { await client.ScanSiteAsync(new ScanSiteRequest() { Site = "Workflow2013_1" }); });
            _ = Task.Run(async () => { await client.ScanSiteAsync(new ScanSiteRequest() { Site = "Workflow2013_2" }); });

            return new StartReply() { Success = true };
            //return base.Start(request, context);
        }

        public override async Task ScanSiteStreaming(ScanSiteRequest request, IServerStreamWriter<StartStatus> responseStream, ServerCallContext context)
        {
            var doneTcs = new TaskCompletionSource<bool>(); // bool is a dummy type

            //logger.LogWarning($"Started for {request.Site} ThreadId : {Thread.CurrentThread.ManagedThreadId}");
            await responseStream.WriteAsync(new StartStatus
            {
                Status = $"{request.Site} : Start. ThreadId : {Thread.CurrentThread.Name}"
            });

            int delay = new Random().Next(500, 10000);
            await Task.Delay(delay);

            //logger.LogWarning($"Step 1 Delay {delay} ThreadId : {Thread.CurrentThread.ManagedThreadId}");
            await responseStream.WriteAsync(new StartStatus
            {
                Status = $"{request.Site} : Step 1 Delay {delay}. ThreadId : {Thread.CurrentThread.Name}"
            });

            await client.StatusAsync(new StatusRequest() { Message = $"Step 1 - Executor ThreadId : {Thread.CurrentThread.ManagedThreadId}" });

            delay = new Random().Next(500, 10000);
            await Task.Delay(delay);

            //logger.LogWarning($"Step 2 Delay {delay} ThreadId : {Thread.CurrentThread.ManagedThreadId}");
            await responseStream.WriteAsync(new StartStatus
            {
                Status = $"{request.Site} : Step 2 Delay {delay}. ThreadId : {Thread.CurrentThread.Name}"
            });
            await client.StatusAsync(new StatusRequest() { Message = $"Step 2 - Executor ThreadId : {Thread.CurrentThread.ManagedThreadId}" });

            delay = new Random().Next(500, 10000);
            await Task.Delay(delay);

            //logger.LogWarning($"Step 3 Delay {delay} ThreadId : {Thread.CurrentThread.ManagedThreadId}");
            await responseStream.WriteAsync(new StartStatus
            {
                Status = $"{request.Site} : Step 3 Delay {delay}. ThreadId : {Thread.CurrentThread.Name}"
            });
            await client.StatusAsync(new StatusRequest() { Message = $"Step 3 - Executor ThreadId : {Thread.CurrentThread.ManagedThreadId}" });

            await responseStream.WriteAsync(new StartStatus
            {
                Status = $"{request.Site} : Done!. ThreadId : {Thread.CurrentThread.Name}"
            });

            await doneTcs.Task;
        }


        public override async Task<ScanSiteReply> ScanSite(ScanSiteRequest request, ServerCallContext context)
        {
            try
            {

                logger.LogWarning($"Started for {request.Site} ThreadId : {Thread.CurrentThread.ManagedThreadId}");

                int delay = new Random().Next(500, 10000);
                await Task.Delay(delay);

                logger.LogWarning($"Step 1 Delay {delay} ThreadId : {Thread.CurrentThread.ManagedThreadId}");
                await client.StatusAsync(new StatusRequest() { Message = $"Step 1 - Executor ThreadId : {Thread.CurrentThread.ManagedThreadId}" });

                delay = new Random().Next(500, 10000);
                await Task.Delay(delay);

                logger.LogWarning($"Step 2 Delay {delay} ThreadId : {Thread.CurrentThread.ManagedThreadId}");
                await client.StatusAsync(new StatusRequest() { Message = $"Step 2 - Executor ThreadId : {Thread.CurrentThread.ManagedThreadId}" });

                delay = new Random().Next(500, 10000);
                await Task.Delay(delay);

                logger.LogWarning($"Step 3 Delay {delay} ThreadId : {Thread.CurrentThread.ManagedThreadId}");
                await client.StatusAsync(new StatusRequest() { Message = $"Step 3 - Executor ThreadId : {Thread.CurrentThread.ManagedThreadId}" });                
            }
            catch (Exception ex)
            {
                //await orchestratorClient.StatusAsync(new StatusRequest() { Message = ex.ToString() });
                return new ScanSiteReply() { Success = false };
            }

            //return base.Start(request, context);
            return new ScanSiteReply() { Success = true };

            //return base.ScanSite(request, context);
        }

    }
}
