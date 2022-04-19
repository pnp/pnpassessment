using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using PnP.Scanning.Core.Authentication;
using Serilog;
using System.Runtime.InteropServices;

namespace PnP.Scanning.Core.Services
{
    /// <summary>
    /// Scanner GRPC server
    /// </summary>
    internal sealed class Scanner : PnPScanner.PnPScannerBase
    {        
        private readonly ScanManager scanManager;
        private readonly SiteEnumerationManager siteEnumerationManager;
        private readonly ReportManager reportManager;
        private readonly TelemetryManager telemetryManager;
        private readonly IHost kestrelWebServer;
        private readonly IDataProtectionProvider dataProtectionProvider;

        public Scanner(ScanManager siteScanManager, SiteEnumerationManager siteEnumeration, ReportManager reports, TelemetryManager telemetry, IHost host, IDataProtectionProvider provider)
        {
            // Kestrel
            kestrelWebServer = host;
            // Scan manager
            scanManager = siteScanManager;
            // Site enumeration
            siteEnumerationManager = siteEnumeration;
            // Report manager
            reportManager = reports;
            // Telemetry manager
            telemetryManager = telemetry;
            // Data Protection Manager
            dataProtectionProvider = provider;
        }

        public override async Task<StatusReply> Status(StatusRequest request, ServerCallContext context)
        {
            Log.Information("Status {Message} received", request.Message);
            // Don't send telemetry event here as status is called automatically in a loop from the CLI
            return await scanManager.GetScanStatusAsync();
        }

        public override async Task<ListReply> List(ListRequest request, ServerCallContext context)
        {
            Log.Information("List request received");
            await telemetryManager.LogEventAsync(Guid.Empty, TelemetryEvent.List);
            return await scanManager.GetScanListAsync(request);
        }

        public override async Task Pause(PauseRequest request, IServerStreamWriter<PauseStatus> responseStream, ServerCallContext context)
        {
            if (!Guid.TryParse(request.Id, out Guid scanId))
            {
                await responseStream.WriteAsync(new PauseStatus
                {
                    Status = $"Passed assessment id {request.Id} is invalid",
                    Type = Constants.MessageError
                });
            }
            else
            {
                // check if the passed scan id is valid one
                if (!request.All && !scanManager.ScanExists(scanId))
                {
                    await responseStream.WriteAsync(new PauseStatus
                    {
                        Status = $"Provided assessment id {scanId} is invalid",
                        Type = Constants.MessageError
                    });

                    Log.Warning("Provided assessment id {ScanId} is not known as running assessment", scanId);
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
                    Status = "Waiting for running web assessments to complete..."
                });

                // Wait for running web scans to complete
                var waitSucceeded = await scanManager.WaitForPendingWebScansAsync(scanId, request.All
#if DEBUG
                    // Don't retry that long in debug mode
                    , maxChecks: 3
#endif
                    );

                if (waitSucceeded)
                {
                    // All waiting web scans finished in time, continue with the pasuing
                    await responseStream.WriteAsync(new PauseStatus
                    {
                        Status = "Running web assessments have completed"
                    });

                    await responseStream.WriteAsync(new PauseStatus
                    {
                        Status = "Implement pausing in assessment database(s)"
                    });

                    // Update scan database(s)
                    await scanManager.PrepareDatabaseForPauseAsync(scanId, request.All);

                    await responseStream.WriteAsync(new PauseStatus
                    {
                        Status = "Assessment database(s) are paused"
                    });

                    // Finalized the pausing 
                    await scanManager.SetPausingStatusAsync(scanId, request.All, Storage.ScanStatus.Paused);

                    await responseStream.WriteAsync(new PauseStatus
                    {
                        Status = "Pausing done"
                    });
                }
                else
                {
                    await responseStream.WriteAsync(new PauseStatus
                    {
                        Status = "Pausing did not happen timely, marking assessment as terminated"
                    });

                    // Start request cancellation to break out of the possible throttling retry loops
                    scanManager.CancelScan(scanId, request.All);

                    // Finalized the pausing 
                    await scanManager.SetPausingStatusAsync(scanId, request.All, Storage.ScanStatus.Terminated);

                    await responseStream.WriteAsync(new PauseStatus
                    {
                        Status = "Assessments was terminated"
                    });
                }

                await telemetryManager.LogScanEventAsync(scanId, TelemetryEvent.Pause);
            }
        }

        public override async Task Restart(RestartRequest request, IServerStreamWriter<RestartStatus> responseStream, ServerCallContext context)
        {
            await responseStream.WriteAsync(new RestartStatus
            {
                Status = "Restarting assessment"
            });

            if (!Guid.TryParse(request.Id, out Guid scanId))
            {
                await responseStream.WriteAsync(new RestartStatus
                {
                    Status = $"Passed assessment id {request.Id} is invalid",
                    Type = Constants.MessageError
                });

                return;
            }

            if (scanManager.ScanExists(scanId))
            {
                await responseStream.WriteAsync(new RestartStatus
                {
                    Status = $"Provided assessment id {scanId} is already running or finished",
                    Type = Constants.MessageError
                });

                Log.Warning("Provided assessment id {ScanId} is already running or finished", scanId);

                return;
            }

            try
            {
                // Restart the scan
                await scanManager.RestartScanAsync(scanId, request, async (message) => 
                {
                    await responseStream.WriteAsync(new RestartStatus
                    {
                        Status = message
                    });
                });

                await responseStream.WriteAsync(new RestartStatus
                {
                    Status = "Assessment restarted"
                });

                await telemetryManager.LogScanEventAsync(scanId, TelemetryEvent.Restart);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error restarting assessment job: {Message}", ex.Message);

                await responseStream.WriteAsync(new RestartStatus
                {
                    Status = $"Assessment job not restarted due to error: {ex.Message}",
                    Type = Constants.MessageError
                });
            }
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public override async Task<Empty> Stop(StopRequest request, ServerCallContext context)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            // Run the stop in a separate thread so that the GRPc client still gets a response
            _ = Task.Run(async () =>
            {
                await telemetryManager.LogEventAsync(Guid.Empty, TelemetryEvent.Stop);
                await kestrelWebServer.StopAsync();
            });
            return new Empty();
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async override Task<PingReply> Ping(Empty request, ServerCallContext context)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            return new PingReply() { UpAndRunning = true, ProcessId = Environment.ProcessId };
        }

        public override async Task Start(StartRequest request, IServerStreamWriter<StartStatus> responseStream, ServerCallContext context)
        {
            try
            {
                Log.Information("Starting Microsoft 365 Assessment");
                await responseStream.WriteAsync(new StartStatus
                {
                    Status = "Starting the Microsoft 365 Assessment"
                });

                // 1. Handle auth
                var authenticationManager = AuthenticationManager.Create(request, dataProtectionProvider);

                await responseStream.WriteAsync(new StartStatus
                {
                    Status = "Microsoft 365 Assessment authentication initialized"
                });

                // 2. Build list of sites to scan
                List<string> sitesToScan = await siteEnumerationManager.EnumerateSiteCollectionsToScanAsync(request, authenticationManager, async (message) =>
                {
                    await responseStream.WriteAsync(new StartStatus
                    {
                        Status = message
                    });
                });

                if (sitesToScan.Count == 0)
                {
                    await responseStream.WriteAsync(new StartStatus
                    {
                        Status = "No sites to assess defined",
                        Type = Constants.MessageWarning
                    });

                    Log.Information("No sites to assess defined");
                }
                else
                {
                    await responseStream.WriteAsync(new StartStatus
                    {
                        Status = "Sites to assess are defined"
                    });

                    // 3. Start the scan
                    var scanId = await scanManager.StartScanAsync(request, authenticationManager, sitesToScan);

                    await responseStream.WriteAsync(new StartStatus
                    {
                        Status = $"Sites to assess are queued up. Assessment id = {scanId}"
                    });

                    await telemetryManager.LogScanEventAsync(scanId, TelemetryEvent.Start);

                    Log.Information("Assessment job started");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error starting assessment job: {Message}", ex.Message);

                await responseStream.WriteAsync(new StartStatus
                {
                    Status = $"Assessment job not started due to error: {ex.Message}",
                    Type = Constants.MessageError
                });
            }
        }

        public async override Task Report(ReportRequest request, IServerStreamWriter<ReportStatus> responseStream, ServerCallContext context)
        {
            try
            {
                if (!Guid.TryParse(request.Id, out Guid scanId))
                {
                    await responseStream.WriteAsync(new ReportStatus
                    {
                        Status = $"Passed assessment id {request.Id} is invalid",
                        Type = Constants.MessageError
                    });

                    return;
                }

                await responseStream.WriteAsync(new ReportStatus
                {
                    Status = $"Exporting report data started"
                });

                Log.Information("Report data export started for assessment {ScanId}", scanId);

                var dataExportPath = await reportManager.ExportReportDataAsync(scanId, request.Path, request.Delimiter);

                await responseStream.WriteAsync(new ReportStatus
                {
                    Status = $"Exporting report data done",
                    ReportPath = dataExportPath
                });
                Log.Information("Report data exported for assessment {ScanId}", scanId);

                if (request.Mode == ReportMode.PowerBI.ToString())
                {
                    await responseStream.WriteAsync(new ReportStatus
                    {
                        Status = $"Start Building Power BI report"
                    });
                    Log.Information("Start Building Power BI report for assessment {ScanId}", scanId);

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        var exportPath = await reportManager.CreatePowerBiReportAsync(scanId, request.Path, request.Delimiter);

                        await responseStream.WriteAsync(new ReportStatus
                        {
                            Status = $"Building Power BI report done",
                            ReportPath = exportPath
                        });
                        Log.Information("Power BI report for assessment {ScanId} is ready", scanId);
                    }
                    else
                    {
                        await responseStream.WriteAsync(new ReportStatus
                        {
                            Status = $"Building Power BI report skipped, this requires a Microsoft Windows OS"
                        });
                        Log.Information("Power BI report for assessment {ScanId} is skipped due to a non Windows OS", scanId);
                    }
                }

                await telemetryManager.LogScanEventAsync(scanId, TelemetryEvent.Report);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating report for this Microsoft 365 Assessment. Error : {Message}", ex.Message);

                await responseStream.WriteAsync(new ReportStatus
                {
                    Status = $"Error creating report for this Microsoft 365 Assessment due to error: {ex.Message}",
                    Type = Constants.MessageError
                });
            }
        }

    }
}
