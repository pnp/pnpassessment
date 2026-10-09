using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Pipeline.Orchestration;
using Serilog;
using System.Runtime.InteropServices;

namespace PnP.Scanning.Core.Services
{
    /// <summary>
    /// Scanner GRPC server
    /// </summary>
    internal sealed class Scanner : PnPScanner.PnPScannerBase
    {        
        private readonly Lazy<ScanManager> legacyScanManager;
        private readonly IServiceProvider services;
        private readonly PipelineCoordinator pipeline;
        private ScanManager scanManager => legacyScanManager.Value;
        private SiteEnumerationManager siteEnumerationManager => services.GetRequiredService<SiteEnumerationManager>();
        private ReportManager reportManager => services.GetRequiredService<ReportManager>();
        private TelemetryManager telemetryManager => services.GetRequiredService<TelemetryManager>();
        private readonly IHost kestrelWebServer;
        private IDataProtectionProvider dataProtectionProvider => services.GetRequiredService<IDataProtectionProvider>();

        public Scanner(PipelineCoordinator coordinator, IHost host, IServiceProvider serviceProvider)
        {
            kestrelWebServer = host;
            services = serviceProvider;
            pipeline = coordinator;
            legacyScanManager = new Lazy<ScanManager>(() => services.GetRequiredService<ScanManager>());
        }

        public override async Task<StatusReply> Status(StatusRequest request, ServerCallContext context)
        {
            Log.Information("Status {Message} received", request.Message);
            // Don't send telemetry event here as status is called automatically in a loop from the CLI
            var reply = await pipeline.StatusAsync(context.CancellationToken);
            if (legacyScanManager.IsValueCreated) reply.Status.AddRange((await scanManager.GetScanStatusAsync()).Status);
            return reply;
        }

        public override async Task<ListReply> List(ListRequest request, ServerCallContext context)
        {
            Log.Information("List request received");
            var reply = await pipeline.ListAsync(request, context.CancellationToken);
            bool all = !request.Running && !request.Paused && !request.Finished && !request.Terminated;
            var legacy = await ScanEnumerationManager.EnumerateScansFromDiskAsync(null,
                all || request.Running, all || request.Paused, all || request.Finished, all || request.Terminated);
            reply.Status.AddRange(legacy.Status);
            if (legacyScanManager.IsValueCreated) await telemetryManager.LogEventAsync(Guid.Empty, TelemetryEvent.List);
            return reply;
        }

        public override Task<PhaseReply> Collect(CollectRequest request, ServerCallContext context) =>
            PipelineCallAsync(() => pipeline.CollectAsync(request, context.CancellationToken));

        public override Task<PhaseReply> Analyze(AnalyzeRequest request, ServerCallContext context) =>
            PipelineCallAsync(() => pipeline.AnalyzeAsync(request, context.CancellationToken));

        public override Task<PhaseReply> StartPipeline(StartPipelineRequest request, ServerCallContext context) =>
            PipelineCallAsync(() => pipeline.StartPipelineAsync(request, context.CancellationToken));

        private static async Task<PhaseReply> PipelineCallAsync(Func<Task<PhaseReply>> action)
        {
            try { return await action(); }
            catch (ArgumentException ex) { throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)); }
            catch (System.Text.Json.JsonException ex) { throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)); }
            catch (KeyNotFoundException ex) { throw new RpcException(new Status(StatusCode.NotFound, ex.Message)); }
            catch (NotSupportedException ex) { throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message)); }
            catch (InvalidOperationException ex) { throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message)); }
            catch (Pipeline.Contracts.SnapshotIntegrityException ex) { throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message)); }
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
                if (request.All || await pipeline.OwnsAssessmentAsync(scanId, context.CancellationToken))
                {
                    try
                    {
                        await pipeline.PauseAsync(scanId, request.All, context.CancellationToken);
                        await responseStream.WriteAsync(new PauseStatus { Status = "Pipeline phases paused at committed checkpoints" });
                    }
                    catch (Exception ex)
                    {
                        await responseStream.WriteAsync(new PauseStatus { Status = ex.Message, Type = Constants.MessageError });
                    }
                    if (!request.All || !legacyScanManager.IsValueCreated) return;
                }
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

            if (await pipeline.OwnsAssessmentAsync(scanId, context.CancellationToken))
            {
                try
                {
                    Guid? runId = string.IsNullOrWhiteSpace(request.RunId) ? null : Guid.Parse(request.RunId);
                    var ticket = await pipeline.RestartAsync(scanId, request.Threads, runId, context.CancellationToken);
                    await responseStream.WriteAsync(new RestartStatus { Status = $"Pipeline run {ticket.RunId} resumed with snapshot {ticket.SnapshotId} and rule {ticket.RuleVersion}" });
                }
                catch (Exception ex)
                {
                    await responseStream.WriteAsync(new RestartStatus { Status = ex.Message, Type = Constants.MessageError });
                }
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

        public override async Task<Empty> Stop(StopRequest request, ServerCallContext context)
        {
            if (!string.IsNullOrWhiteSpace(request.Id))
            {
                if (!Guid.TryParse(request.Id, out var id)) throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid assessment ID."));
                if (await pipeline.OwnsAssessmentAsync(id, context.CancellationToken))
                    await pipeline.TerminateAsync(id, context.CancellationToken);
                else
                {
                    if (!scanManager.ScanExists(id)) throw new RpcException(new Status(StatusCode.NotFound, "Assessment is not running."));
                    scanManager.CancelScan(id, false);
                    await scanManager.SetPausingStatusAsync(id, false, Storage.ScanStatus.Terminated);
                }
                return new Empty();
            }
            // Run the stop in a separate thread so that the GRPc client still gets a response
            _ = Task.Run(async () =>
            {
                await pipeline.StopAsync(CancellationToken.None);
                if (legacyScanManager.IsValueCreated) await telemetryManager.LogEventAsync(Guid.Empty, TelemetryEvent.Stop);
                await kestrelWebServer.StopAsync();
            });
            return new Empty();
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async override Task<PingReply> Ping(Empty request, ServerCallContext context)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            return new PingReply() { UpAndRunning = true, ProcessId = Environment.ProcessId, SupportsPipeline = true };
        }

        public override async Task Start(StartRequest request, IServerStreamWriter<StartStatus> responseStream, ServerCallContext context)
        {
            try
            {
                Log.Information("Starting Microsoft 365 Assessment");
                await responseStream.WriteAsync(new StartStatus
                {
                    Status = "Starting the Microsoft 365 Assessment (legacy)"
                });

                // 1. Handle auth
                var authenticationManager = AuthenticationManager.Create(request, dataProtectionProvider);

                await responseStream.WriteAsync(new StartStatus
                {
                    Status = "Microsoft 365 Assessment authentication initialized"
                });

                // 2. Build list of sites to scan
                var discoveryEvidence = new List<PnP.Scanning.Core.Storage.ClassicPageDiscovery>();
                List<string> sitesToScan = await siteEnumerationManager.EnumerateSiteCollectionsToScanAsync(request, authenticationManager, async (message) =>
                {
                    await responseStream.WriteAsync(new StartStatus
                    {
                        Status = message
                    });
                }, discoveryEvidence);

                if (sitesToScan.Count == 0 && discoveryEvidence.Count == 0)
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
                    var scanId = await scanManager.StartScanAsync(request, authenticationManager, sitesToScan, discoveryEvidence);

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

                if (await pipeline.OwnsAssessmentAsync(scanId, context.CancellationToken))
                {
                    Guid? runId = string.IsNullOrEmpty(request.AnalysisRunId) ? null : Guid.TryParse(request.AnalysisRunId, out var parsed)
                        ? parsed : throw new ArgumentException("A valid analysis run ID is required.");
                    var exported = await ClassicPageReportExporter.ExportAsync(services.GetRequiredService<PnP.Scanning.Core.Storage.Pipeline.PipelineStore>(),
                        scanId, runId, request.Path, request.Delimiter, request.Mode == ReportMode.PowerBI.ToString(), context.CancellationToken);
                    await responseStream.WriteAsync(new ReportStatus { Status = $"Classic Page analysis {exported.RunId} exported", ReportPath = exported.Path });
                    return;
                }
                if (!string.IsNullOrEmpty(request.AnalysisRunId)) throw new NotSupportedException("Legacy assessments do not have analysis run IDs.");

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
