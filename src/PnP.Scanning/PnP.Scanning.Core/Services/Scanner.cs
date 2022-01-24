using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace PnP.Scanning.Core.Services
{
    /// <summary>
    /// Scanner GRPC server
    /// </summary>
    internal sealed class Scanner : PnPScanner.PnPScannerBase
    {        
        private readonly ScanManager scanManager;
        private readonly SiteEnumerationManager siteEnumerationManager;
        private readonly IHost kestrelWebServer;

        public Scanner(ScanManager siteScanManager, SiteEnumerationManager siteEnumeration, IHost host)
        {
            // Kestrel
            kestrelWebServer = host;
            // Scan manager
            scanManager = siteScanManager;
            // Site enumeration
            siteEnumerationManager = siteEnumeration;
        }

        public override async Task<StatusReply> Status(StatusRequest request, ServerCallContext context)
        {
            Log.Information("Status {Message} received", request.Message);
            return await scanManager.GetScanStatusAsync();
        }

        public override async Task<ListReply> List(ListRequest request, ServerCallContext context)
        {
            Log.Information("List request received");
            return await scanManager.GetScanListAsync(request);
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
            Log.Information("Starting scan");
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
            List<string> sitesToScan = await siteEnumerationManager.EnumerateSiteCollectionsToScanAsync(request);

            if (sitesToScan.Count == 0)
            {
                await responseStream.WriteAsync(new StartStatus
                {
                    Status = "No sites to scan defined"
                });

                Log.Information("No sites to scan defined");
            }
            else
            {
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

                Log.Information("Scan job started");
            }
        }
    }
}
