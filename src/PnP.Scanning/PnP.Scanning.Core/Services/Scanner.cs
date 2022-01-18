using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PnP.Scanning.Core.Services
{
    /// <summary>
    /// Scanner GRPC server
    /// </summary>
    internal sealed class Scanner : PnPScanner.PnPScannerBase
    {        
        private readonly ILogger logger;
        private readonly ScanManager scanManager;
        private readonly IHost kestrelWebServer;

        public Scanner(ILoggerFactory loggerFactory, ScanManager siteScanManager, IHost host)
        {
            // Configure logging
            logger = loggerFactory.CreateLogger<Scanner>();
            // Kestrel
            kestrelWebServer = host;
            // Scan manager
            scanManager = siteScanManager;
        }

        public override async Task<StatusReply> Status(StatusRequest request, ServerCallContext context)
        {
            logger.LogInformation($"Status {request.Message} received");
            return await scanManager.GetScanStatusAsync();
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public override async Task<Empty> Stop(StopRequest request, ServerCallContext context)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            // Run the stop in a separate thread so that the GRPc client still gets a response
            _ = Task.Run(async () =>
            {
                await kestrelWebServer.StopAsync();
            });
            return new Empty();
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async override Task<PingReply> Ping(Empty request, ServerCallContext context)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            return new PingReply() { UpAndRunning = true };
        }

        public override async Task StartStreaming(StartRequest request, IServerStreamWriter<StartStatus> responseStream, ServerCallContext context)
        {
            await responseStream.WriteAsync(new StartStatus
            {
                Status = "Starting"
            });

            // 1. Handle auth

            await responseStream.WriteAsync(new StartStatus
            {
                Status = "Authenticated"
            });

            // 2. Build list of sites to scan
            List<string> sitesToScan = new();

            for (int i = 0; i < 10; i++)
            {
                sitesToScan.Add($"https://bertonline.sharepoint.com/sites/prov-{i}");
            }

            await responseStream.WriteAsync(new StartStatus
            {
                Status = "Sites to scan are defined"
            });

            // 3. Start the scan
            var scanId = await scanManager.StartScanAsync(request, sitesToScan);

            await responseStream.WriteAsync(new StartStatus
            {
                Status = $"Sites to scan are queued up. Scan id = {scanId}"
            });            
        }
    }
}
