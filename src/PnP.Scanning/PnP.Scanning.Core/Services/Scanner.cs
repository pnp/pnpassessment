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

        public override async Task Pause(PauseRequest request, IServerStreamWriter<PauseStatus> responseStream, ServerCallContext context)
        {
            if (!Guid.TryParse(request.Id, out Guid scanId))
            {
                await responseStream.WriteAsync(new PauseStatus
                {
                    Status = $"Passed scan id {request.Id} is invalid"
                });
            }
            else
            {
                // check if the passed scan id is valid one
                if (!request.All && !scanManager.ScanExists(scanId))
                {
                    await responseStream.WriteAsync(new PauseStatus
                    {
                        Status = $"Provided scan id {scanId} is invalid"
                    });

                    Log.Warning("Provided scan id {ScanId} is not known as running scan", scanId);
                    return;
                }

                await responseStream.WriteAsync(new PauseStatus
                {
                    Status = "Start pausing"
                });

                // Start the pausing 
                await scanManager.SetPausingStatusAsync(scanId, request.All, Storage.ScanStatus.Pausing);

                await responseStream.WriteAsync(new PauseStatus
                {
                    Status = "Waiting for running web scans to complete..."
                });

                // Wait for running web scans to complete
                await scanManager.WaitForPendingWebScansAsync(scanId, request.All);

                await responseStream.WriteAsync(new PauseStatus
                {
                    Status = "Running web scans have completed"
                });

                await responseStream.WriteAsync(new PauseStatus
                {
                    Status = "Implement pausing in scan database(s)"
                });

                // Update scan database(s)
                await scanManager.PrepareDatabaseForPauseAsync(scanId, request.All);

                await responseStream.WriteAsync(new PauseStatus
                {
                    Status = "Scan database(s) are paused"
                });

                // Finalized the pausing 
                await scanManager.SetPausingStatusAsync(scanId, request.All, Storage.ScanStatus.Paused);

                await responseStream.WriteAsync(new PauseStatus
                {
                    Status = "Pausing done"
                });
            }
        }

        public override async Task Restart(RestartRequest request, IServerStreamWriter<RestartStatus> responseStream, ServerCallContext context)
        {
            await responseStream.WriteAsync(new RestartStatus
            {
                Status = "Restarting scan"
            });

            if (!Guid.TryParse(request.Id, out Guid scanId))
            {
                await responseStream.WriteAsync(new RestartStatus
                {
                    Status = $"Passed scan id {request.Id} is invalid"
                });

                return;
            }

            if (scanManager.ScanExists(scanId))
            {
                await responseStream.WriteAsync(new RestartStatus
                {
                    Status = $"Provided scan id {scanId} is already running or finished"
                });

                Log.Warning("Provided scan id {ScanId} is already running or finished", scanId);

                return;
            }

            try
            {
                // Restart the scan
                await scanManager.RestartScanAsync(scanId);

                await responseStream.WriteAsync(new RestartStatus
                {
                    Status = "Scan restarted"
                });
            }
            catch (Exception ex)
            {
                await responseStream.WriteAsync(new RestartStatus
                {
                    Status = $"Scan job not restarted due to error: {ex.Message}",
                    Type = Constants.MessageError
                });

                Log.Warning("Scan job not started due to error: {Error}", ex.Message);
            }
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

        public override async Task Start(StartRequest request, IServerStreamWriter<StartStatus> responseStream, ServerCallContext context)
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
                try
                {
                    var scanId = await scanManager.StartScanAsync(request, sitesToScan);

                    await responseStream.WriteAsync(new StartStatus
                    {
                        Status = $"Sites to scan are queued up. Scan id = {scanId}"
                    });

                    Log.Information("Scan job started");

                }
                catch (Exception ex)
                {
                    await responseStream.WriteAsync(new StartStatus
                    {
                        Status = $"Scan job not started due to error: {ex.Message}",
                        Type = Constants.MessageError
                    });

                    Log.Warning("Scan job not started due to error: {Error}", ex.Message);
                }
            }
        }
    }
}
