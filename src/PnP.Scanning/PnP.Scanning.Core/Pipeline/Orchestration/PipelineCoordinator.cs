using PnP.Scanning.Core.ClassicPages;
#nullable enable
using System.Collections.Concurrent;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;

namespace PnP.Scanning.Core.Pipeline.Orchestration;

/// <summary>
/// Schedules internal collection and offline-analysis phases behind the existing public lifecycle.
/// </summary>
internal sealed class PipelineCoordinator(
    PipelineStore store,
    ModuleRegistry registry,
    ICollectionEnvironment collectionEnvironment) : IHostedService
{
    private readonly SemaphoreSlim scheduling = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ActiveExecution> active = new();
    private readonly object lifecycle = new();
    private volatile bool stopping;

    internal int ActiveCount => active.Count;

    /// <summary>
    /// Checks whether the assessment currently owns an in-memory phase execution.
    /// </summary>
    internal bool IsActive(Guid assessmentId) => active.ContainsKey(assessmentId);

    /// <summary>
    /// Serializes retained assessment admission with pipeline capacity checks.
    /// </summary>
    internal async Task<T> AdmitLegacyAsync<T>(Func<Task<T>> action)
    {
        await scheduling.WaitAsync();
        try
        {
            CheckCapacity();
            return await action();
        }
        finally
        {
            scheduling.Release();
        }
    }

    /// <summary>
    /// Marks persisted unfinished pipeline runs interrupted before accepting work.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken) => store.MarkInterruptedRunsAsync(cancellationToken);

    /// <summary>
    /// Stops admission, cancels active phases, and waits for durable settlement.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ActiveExecution[] executions;
        lock (lifecycle)
        {
            stopping = true;
            executions = active.Values.ToArray();
        }

        foreach (ActiveExecution? execution in executions)
            await RequestStopAsync(execution, ScanStatus.Terminated, cancellationToken);
    }

    /// <summary>
    /// Queues internal collection without selecting or constructing an analyzer.
    /// </summary>
    internal Task<PhaseReply> CollectAsync(CollectRequest request, CancellationToken token = default) =>
        QueueCollectionAsync(request, null, null, token);

    /// <summary>
    /// Queues internal collection followed by the pinned offline-analysis rule.
    /// </summary>
    internal Task<PhaseReply> StartPipelineAsync(StartPipelineRequest request, CancellationToken token = default) =>
        QueueCollectionAsync(
            request.Collection ?? throw new ArgumentException("A collection request is required."),
            request.RuleVersion,
            ParseParameters(request.AnalysisParametersJson),
            token);

    /// <summary>
    /// Validates registrations before acquisition, persists fixed scope and run identities, and launches collection.
    /// </summary>
    private async Task<PhaseReply> QueueCollectionAsync(
        CollectRequest request,
        string? ruleVersion,
        VersionedJson? analysisParameters,
        CancellationToken token,
        Guid? existingAssessment = null)
    {
        // Metadata validation occurs before database creation, factories, credentials or online services.
        CollectionRegistration? collector = registry.GetCollector(request.Module);
        VersionedJson? parameters = ParseParameters(request.ParametersJson);
        collector.ValidateParameters?.Invoke(parameters);
        AnalysisRegistration? analyzer = analysisParameters == null
            ? null
            : registry.GetAnalyzer(request.Module, ruleVersion, collector.InputVersion);
        if (analyzer?.PinParameters != null) analysisParameters = analyzer.PinParameters(analysisParameters!);
        analyzer?.ValidateParameters?.Invoke(analysisParameters!);
        StartRequest? options = request.CollectionOptions?.Clone() ??
                                throw new ArgumentException("Collection options are required.");
        collector.ValidateOptions?.Invoke(options);
        ValidateThreads(options.Threads);

        await scheduling.WaitAsync(token);
        try
        {
            CheckCapacity(existingAssessment);
            Guid assessmentId = existingAssessment ?? Guid.NewGuid();
            Guid snapshotId = Guid.NewGuid();
            Guid rootId = Guid.NewGuid();
            Guid? analysisId = analyzer == null ? (Guid?)null : Guid.NewGuid();
            StartRequest? protectedOptions = collectionEnvironment.Protect(options);
            PhaseRunRow? root = NewRun(
                assessmentId,
                rootId,
                snapshotId,
                collector.ModuleKey,
                collector.InputVersion,
                analyzer == null ? PhaseKind.Collection : PhaseKind.Pipeline,
                options.Threads,
                parameters,
                analysisParameters);
            root.CurrentPhase = PhaseKind.Collection;
            root.RuleVersion = analyzer?.RuleVersion;
            root.ParametersJson = (PipelineRunSettings.Read(root) with
            {
                AnalysisRunId = analysisId, CollectionOptionsJson = JsonFormatter.Default.Format(protectedOptions),
            }).Serialize();
            PhaseRunRow? child = null;
            if (analyzer != null)
            {
                child = NewRun(
                    assessmentId,
                    Guid.NewGuid(),
                    snapshotId,
                    collector.ModuleKey,
                    collector.InputVersion,
                    PhaseKind.Collection,
                    options.Threads,
                    parameters,
                    analysisParameters);
                child.ParentRunId = rootId;
            }

            VersionedJson? scope = VersionedJson.From(
                new
                {
                    options.Tenant,
                    options.Environment,
                    options.SitesList,
                    options.SitesFile,
                    options.AdminCenterUrl,
                    options.MySiteHostUrl,
                    properties =
                        options.Properties.OrderBy(x => x.Property)
                            .Select(x => new { x.Property, x.Type, x.Value })
                            .ToArray(),
                    moduleParameters = parameters.Value,
                });
            await store.CreateCollectionAsync(
                new Storage.Scan
                {
                    ScanId = assessmentId,
                    CLIMode =
                        collector.ModuleKey == ClassicPageModule.Key
                            ? Mode.Classic.ToString()
                            : $"Pipeline:{collector.ModuleKey}",
                    StartDate = DateTime.UtcNow,
                    Status = ScanStatus.Queued,
                    CLIThreads = options.Threads,
                    Version = VersionManager.GetCurrentVersion(),
                    CLITenant = options.Tenant,
                    CLITenantId = options.TenantId,
                    CLIEnvironment = options.Environment,
                    CLIAuthMode = options.AuthMode,
                    CLIApplicationId = options.ApplicationId,
                    CLISiteList = options.SitesList,
                    CLISiteFile = options.SitesFile,
                    CLICertPath = options.CertPath,
                    CLICertFile = options.CertFile,
                    PreScanStatus = SiteWebStatus.Queued,
                    PostScanStatus = SiteWebStatus.Queued,
                },
                new SourceSnapshotRow
                {
                    AssessmentId = assessmentId,
                    SnapshotId = snapshotId,
                    ModuleKey = collector.ModuleKey,
                    InputVersion = collector.InputVersion,
                    ScopeJson = scope.Json,
                    CreatedAtUtc = DateTime.UtcNow,
                },
                root,
                child,
                token,
                adoptLegacy: existingAssessment.HasValue);
            await LaunchAsync(root);
            return Ticket(root);
        }
        finally
        {
            scheduling.Release();
        }
    }

    /// <summary>
    /// Adopts a paused historical Classic assessment by explicitly acquiring a new raw snapshot under its existing ID.
    /// </summary>
    internal async Task<PhaseReply?> RestartLegacyClassicAsync(
        Guid assessment,
        int threads,
        Func<string, string> unprotectPassword,
        CancellationToken token)
    {
        if (!File.Exists(store.DatabasePath(assessment))) return null;
        await store.EnsureDatabaseAsync(assessment, token);
        using ScanContext? db = store.CreateContext(assessment, readOnly: true);
        Storage.Scan? saved = await db.Scans.AsNoTracking().SingleOrDefaultAsync(x => x.ScanId == assessment, token);
        if (saved?.CLIMode != Mode.Classic.ToString()) return null;
        StartRequest? options = new StartRequest
        {
            Mode = saved.CLIMode,
            Tenant = saved.CLITenant ?? "",
            TenantId = saved.CLITenantId ?? "",
            Environment = saved.CLIEnvironment ?? "",
            AuthMode = saved.CLIAuthMode ?? "",
            ApplicationId = saved.CLIApplicationId ?? "",
            CertPath = saved.CLICertPath ?? "",
            CertFile = saved.CLICertFile ?? "",
            Threads = threads > 0 ? threads : saved.CLIThreads,
            SitesList = saved.CLISiteList ?? "",
            SitesFile = saved.CLISiteFile ?? "",
        };
        foreach (Property? property in
                 await db.Properties.AsNoTracking().Where(x => x.ScanId == assessment).ToListAsync(token))
            options.Properties.Add(
                new PropertyRequest { Property = property.Name, Type = property.Type, Value = property.Value });
        if (!ClassicPageModule.Handles(options)) return null;
        if (saved.Status is not (ScanStatus.Paused or ScanStatus.Terminated))
            throw new InvalidOperationException("Only a paused or terminated Classic assessment can restart.");
        if (!string.IsNullOrEmpty(saved.CLICertFilePassword))
            options.CertPassword = unprotectPassword(saved.CLICertFilePassword);
        // Historical assessments have no sealed raw source. Reacquire into a new snapshot, never pretend legacy rows are raw evidence.
        return await QueueCollectionAsync(
            new CollectRequest { Module = ClassicPageModule.Key, CollectionOptions = options },
            ClassicPageModule.RuleVersion,
            VersionedJson.Empty,
            token,
            assessment);
    }

    /// <summary>
    /// Queues detached analysis of an explicitly selected sealed snapshot without resolving online services.
    /// </summary>
    internal async Task<PhaseReply> AnalyzeAsync(AnalyzeRequest request, CancellationToken token = default)
    {
        Guid assessmentId = ParseId(request.Id, "assessment");
        Guid snapshotId = ParseId(request.SnapshotId, "snapshot");
        SnapshotInfo? snapshot = await store.OpenSnapshotAsync(assessmentId, snapshotId, token);
        AnalysisRegistration? analyzer = registry.GetAnalyzer(
            snapshot.ModuleKey,
            request.RuleVersion,
            snapshot.InputVersion);
        VersionedJson? parameters = ParseParameters(request.ParametersJson);
        if (analyzer.PinParameters != null) parameters = analyzer.PinParameters(parameters);
        analyzer.ValidateParameters?.Invoke(parameters);
        PhaseRunRow? previous = await store.LatestRootAsync(assessmentId, token);
        int threads = request.Threads == 0
            ? previous == null ? 1 : PipelineRunSettings.Read(previous).Threads
            : request.Threads;
        ValidateThreads(threads);

        await scheduling.WaitAsync(token);
        try
        {
            CheckCapacity(assessmentId);
            Guid id = Guid.NewGuid();
            PhaseRunRow? root = NewRun(
                assessmentId,
                id,
                snapshotId,
                snapshot.ModuleKey,
                snapshot.InputVersion,
                PhaseKind.Analysis,
                threads,
                parameters,
                parameters);
            root.ParametersJson = (PipelineRunSettings.Read(root) with { AnalysisRunId = id }).Serialize();
            root.RuleVersion = analyzer.RuleVersion;
            root.TotalRecords = snapshot.ObservationIds.Count;
            await store.CreateAnalysisAsync(root, token);
            await LaunchAsync(root);
            return Ticket(root);
        }
        finally
        {
            scheduling.Release();
        }
    }

    /// <summary>
    /// Resumes a paused or terminated root with its original versions, input, and checkpoints.
    /// </summary>
    internal async Task<PhaseReply> RestartAsync(
        Guid assessmentId,
        int threads = 0,
        Guid? runId = null,
        CancellationToken token = default)
    {
        if (threads != 0) ValidateThreads(threads);
        await scheduling.WaitAsync(token);
        try
        {
            CheckCapacity(assessmentId);
            IReadOnlyList<PhaseRunRow>? runs = await store.GetRunsAsync(assessmentId, token);
            PhaseRunRow? root = runId == null
                ? runs.LastOrDefault(x => x.ParentRunId == null)
                : runs.SingleOrDefault(x => x.RunId == runId && x.ParentRunId == null);
            if (root == null)
                throw new NotSupportedException(
                    "This assessment has no such pipeline run; use the legacy restart for legacy assessments.");
            if (root.Status is not (ScanStatus.Paused or ScanStatus.Terminated))
                throw new InvalidOperationException($"Cannot restart a run whose status is {root.Status}.");
            // Never substitute the current rule for the one recorded on the original run.
            if (root.Kind != PhaseKind.Collection)
            {
                AnalysisRegistration? analyzer = registry.GetAnalyzer(
                    root.ModuleKey,
                    root.RuleVersion,
                    root.InputVersion);
                analyzer.ValidateParameters?.Invoke(
                    new VersionedJson(PipelineRunSettings.Read(root).AnalysisParametersJson));
            }

            if (root.CurrentPhase == PhaseKind.Collection)
            {
                CollectionRegistration? collector = registry.GetCollector(root.ModuleKey, root.InputVersion);
                collector.ValidateParameters?.Invoke(
                    new VersionedJson(PipelineRunSettings.Read(root).CollectionParametersJson));
            }
            else
                await store.OpenSnapshotAsync(assessmentId, root.SnapshotId, token);

            await store.QueueRestartAsync(assessmentId, root.RunId, threads, token);
            await LaunchAsync(root);
            return Ticket(root);
        }
        finally
        {
            scheduling.Release();
        }
    }

    /// <summary>
    /// Cancels selected active phases and waits for paused checkpoint settlement.
    /// </summary>
    internal async Task PauseAsync(Guid assessmentId, bool all, CancellationToken token = default)
    {
        ActiveExecution[]? selected = all
            ? active.Values.ToArray()
            : active.TryGetValue(assessmentId, out ActiveExecution? execution)
                ? [execution]
                : throw new InvalidOperationException("The selected pipeline assessment is not running.");
        foreach (ActiveExecution? item in selected) await RequestStopAsync(item, ScanStatus.Paused, token);
    }

    /// <summary>
    /// Cancels one active assessment while keeping the engine available.
    /// </summary>
    internal async Task TerminateAsync(Guid assessmentId, CancellationToken token = default)
    {
        if (!active.TryGetValue(assessmentId, out ActiveExecution? execution))
            throw new InvalidOperationException("The selected pipeline assessment is not running.");
        await RequestStopAsync(execution, ScanStatus.Terminated, token);
    }

    /// <summary>
    /// Serializes cancellation and persists the requested settlement, reporting a module that fails to settle.
    /// </summary>
    private async Task RequestStopAsync(ActiveExecution execution, ScanStatus desired, CancellationToken token)
    {
        await execution.Control.WaitAsync(token);
        try
        {
            if (execution.Settled) return;
            execution.RequestedStatus = desired;
            execution.Cancellation.Cancel();
            await store.SetPausingAsync(execution.Root.AssessmentId, execution.Root.RunId, token);
        }
        finally
        {
            execution.Control.Release();
        }

        try
        {
            await execution.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        }
        catch (TimeoutException)
        {
            await execution.Control.WaitAsync(token);
            try
            {
                if (execution.Settled) return;
                execution.RequestedStatus = ScanStatus.Terminated;
                await store.SettleRootAsync(
                    execution.Root.AssessmentId,
                    execution.Root.RunId,
                    ScanStatus.Terminated,
                    "A module did not settle cancellation within 30 seconds.",
                    token);
            }
            finally
            {
                execution.Control.Release();
            }

            throw;
        }
    }

    /// <summary>
    /// Rejects stopping hosts, concurrent assessment execution, and exhausted shared capacity.
    /// </summary>
    private void CheckCapacity(Guid? assessmentId = null)
    {
        if (stopping) throw new InvalidOperationException("Assessment engine is stopping.");
        if (assessmentId != null && active.ContainsKey(assessmentId.Value))
            throw new InvalidOperationException(
                "An assessment already has an active phase; pause or finish it before starting another run.");
        if (active.Count + ScanManager.LegacyScansRunning >= ScanManager.MaxParallelScans)
            throw new InvalidOperationException("Max number of parallel assessments reached.");
    }

    /// <summary>
    /// Registers one execution or durably terminates it when engine shutdown wins admission.
    /// </summary>
    private async Task LaunchAsync(PhaseRunRow root)
    {
        ActiveExecution? execution = new ActiveExecution(root);
        lock (lifecycle)
        {
            if (!stopping)
            {
                if (!active.TryAdd(root.AssessmentId, execution))
                    throw new InvalidOperationException("Assessment already has an active phase.");
                _ = Task.Run(() => ExecuteAsync(execution));
                return;
            }
        }

        execution.Cancellation.Dispose();
        await store.SettleRootAsync(
            root.AssessmentId,
            root.RunId,
            ScanStatus.Terminated,
            "Engine stopped before execution was admitted.");
        throw new InvalidOperationException("Assessment engine is stopping.");
    }

    /// <summary>
    /// Collects and seals source before detached analysis, compatibility publication, and durable lifecycle settlement.
    /// </summary>
    private async Task ExecuteAsync(ActiveExecution execution)
    {
        PhaseRunRow? root = execution.Root;
        CancellationToken token = execution.Cancellation.Token;
        Exception? failure = null;
        try
        {
            IReadOnlyList<PhaseRunRow>? runs = await store.GetRunsAsync(root.AssessmentId, token);
            root = runs.Single(x => x.RunId == root.RunId);
            if (root.CurrentPhase == PhaseKind.Collection)
            {
                PhaseRunRow? phase = root.Kind == PhaseKind.Collection
                    ? root
                    : runs.Single(x => x.ParentRunId == root.RunId && x.Kind == PhaseKind.Collection);
                CollectionRegistration? registration = registry.GetCollector(root.ModuleKey, root.InputVersion);
                ICollectionModule? module = registration.Create();
                if (!module.ModuleKey.Equals(registration.ModuleKey, StringComparison.OrdinalIgnoreCase) ||
                    module.InputVersion != registration.InputVersion)
                    throw new InvalidOperationException(
                        "Collection implementation differs from its registered contract.");
                await store.SetRunningAsync(root.AssessmentId, root.RunId, phase.RunId, token);
                PipelineRunSettings? settings = PipelineRunSettings.Read(root);
                string? savedOptions = settings.CollectionOptionsJson ??
                                       throw new InvalidDataException("Saved collection options are absent.");
                StartRequest? options =
                    collectionEnvironment.Restore(JsonParser.Default.Parse<StartRequest>(savedOptions));
                options.Threads = settings.Threads;
                ISnapshotWriter? writer = store.SnapshotWriter(root.AssessmentId, root.SnapshotId, phase.RunId);
                ICollectionJournal? journal = store.CollectionJournal(root.AssessmentId, root.SnapshotId, phase.RunId);
                CollectionContext? context = new CollectionContext(
                    options,
                    new VersionedJson(settings.CollectionParametersJson),
                    phase.CheckpointJson == null ? null : new VersionedJson(phase.CheckpointJson),
                    collectionEnvironment,
                    root.AssessmentId,
                    journal);
                await foreach (CollectionRecord? record in module.CollectAsync(context, token).WithCancellation(token))
                    await writer.CommitAsync(record, token);
                token.ThrowIfCancellationRequested();
                if (module is ICollectionSnapshotValidator validator) await validator.ValidateAsync(journal, token);
                await writer.SealAsync(token);
            }

            if (root.Kind != PhaseKind.Collection)
            {
                token.ThrowIfCancellationRequested();
                // After a sealed checkpoint this path never resolves a collector or restores credentials.
                AnalysisRegistration? registration = registry.GetAnalyzer(
                    root.ModuleKey,
                    root.RuleVersion,
                    root.InputVersion);
                IAnalysisModule? module = registration.Create();
                if (!module.ModuleKey.Equals(registration.ModuleKey, StringComparison.OrdinalIgnoreCase) ||
                    module.RuleVersion != registration.RuleVersion)
                    throw new InvalidOperationException(
                        "Analysis implementation differs from its registered contract.");
                PhaseRunRow? phase = await store.PrepareAnalysisChildAsync(root, token);
                if (phase.Status != ScanStatus.Finished)
                {
                    await store.SetRunningAsync(root.AssessmentId, root.RunId, phase.RunId, token);
                    await new AnalysisExecutor().ExecuteAsync(
                        module,
                        store.SnapshotReader(root.AssessmentId, root.SnapshotId),
                        store.ResultWriter(root.AssessmentId, phase.RunId),
                        new VersionedJson(PipelineRunSettings.Read(root).AnalysisParametersJson),
                        PipelineRunSettings.Read(root).Threads,
                        token);
                    if (module is IAnalysisFinalizer finalizer)
                    {
                        await finalizer.FinalizeAsync(
                            store.SnapshotReader(root.AssessmentId, root.SnapshotId),
                            store.ResultReader(root.AssessmentId, phase.RunId),
                            store.ReportWriter(root.AssessmentId, phase.RunId),
                            new VersionedJson(PipelineRunSettings.Read(root).AnalysisParametersJson),
                            token);
                        await store.FinishAnalysisAsync(root.AssessmentId, phase.RunId, token);
                    }
                    else
                        await store.FinishResultsAsync(root.AssessmentId, phase.RunId, token);
                }
                else
                    await store.OpenSnapshotAsync(root.AssessmentId, root.SnapshotId, token);

                if (root.ModuleKey == ClassicPageModule.Key)
                    await store.MaterializeClassicAsync(root.AssessmentId, phase.RunId, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
            Serilog.Log.Error(ex, "Pipeline run {RunId} failed", root.RunId);
        }

        await execution.Control.WaitAsync();
        try
        {
            ScanStatus status = execution.RequestedStatus ??
                                (failure == null ? ScanStatus.Finished : ScanStatus.Terminated);
            string? error = failure?.ToString() ??
                            (status == ScanStatus.Terminated ? "Execution was canceled by stop." : null);
            await store.SettleRootAsync(root.AssessmentId, root.RunId, status, error);
            execution.Settled = true;
            active.TryRemove(root.AssessmentId, out _);
            execution.Completion.TrySetResult(status);
        }
        catch (Exception ex)
        {
            execution.Settled = true;
            active.TryRemove(root.AssessmentId, out _);
            execution.Completion.TrySetException(ex);
            Serilog.Log.Error(ex, "Pipeline run {RunId} could not persist settlement", root.RunId);
        }
        finally
        {
            execution.Control.Release();
            execution.Cancellation.Dispose();
        }
    }

    /// <summary>
    /// Waits for active settlement or returns the latest persisted root status.
    /// </summary>
    internal async Task<ScanStatus> WaitForCompletionAsync(Guid assessmentId, CancellationToken token = default)
    {
        if (active.TryGetValue(assessmentId, out ActiveExecution? execution))
            return await execution.Completion.Task.WaitAsync(token);
        return (await store.LatestRootAsync(assessmentId, token))?.Status ??
               throw new KeyNotFoundException("Assessment is unknown.");
    }

    /// <summary>
    /// Determines whether persisted phase rows own the assessment lifecycle.
    /// </summary>
    internal async Task<bool> OwnsAssessmentAsync(Guid assessmentId, CancellationToken token = default)
    {
        if (!File.Exists(store.DatabasePath(assessmentId))) return false;
        return (await store.GetRunsAsync(assessmentId, token)).Count > 0;
    }

    /// <summary>
    /// Validates completed Classic analysis and materializes its existing-report compatibility projection.
    /// </summary>
    internal async Task EnsureReportReadyAsync(Guid assessmentId, CancellationToken token)
    {
        PhaseRunRow? root = await store.LatestRootAsync(assessmentId, token);
        if (root == null || root.ModuleKey != ClassicPageModule.Key || root.Status != ScanStatus.Finished)
            throw new InvalidOperationException(
                "The selected assessment has not completed Classic collection and analysis.");
        Guid runId = PipelineRunSettings.Read(root).AnalysisRunId ??
                     throw new InvalidOperationException("The selected assessment has no completed analysis run.");
        await store.MaterializeClassicAsync(assessmentId, runId, token);
    }

    /// <summary>
    /// Projects persisted root phases into existing assessment list responses.
    /// </summary>
    internal async Task<ListReply> ListAsync(ListRequest request, CancellationToken token = default)
    {
        bool all = !request.Running && !request.Paused && !request.Finished && !request.Terminated;
        ListReply? reply = new ListReply();
        foreach (PhaseRunRow? run in await store.ListRootsAsync(token))
        {
            if (!(all || request.Running && run.Status is ScanStatus.Running or ScanStatus.Queued ||
                  request.Paused && run.Status is ScanStatus.Paused or ScanStatus.Pausing ||
                  request.Finished && run.Status == ScanStatus.Finished ||
                  request.Terminated && run.Status == ScanStatus.Terminated))
                continue;
            reply.Status.Add(
                new ListScanResponse
                {
                    Id = run.AssessmentId.ToString(),
                    Mode = run.ModuleKey == ClassicPageModule.Key ? Mode.Classic.ToString() : run.ModuleKey,
                    Status = run.Status.ToString(),
                    ExecutionPath = "pipeline",
                    Phase = run.CurrentPhase.ToString(),
                    SnapshotId = run.SnapshotId.ToString(),
                    RunId = run.RunId.ToString(),
                    AnalysisRunId = PipelineRunSettings.Read(run).AnalysisRunId?.ToString() ?? "",
                    RecordsCompleted = run.CompletedRecords,
                    RecordsTotal = run.TotalRecords,
                    ErrorCount = run.ErrorCount,
                    ErrorSummary = run.LastError ?? "",
                    // SQLite stores the UTC value but does not preserve DateTime.Kind on materialization.
                    ScanStarted = Timestamp.FromDateTime(DateTime.SpecifyKind(run.CreatedAtUtc, DateTimeKind.Utc)),
                    ScanEnded = Timestamp.FromDateTime(
                        DateTime.SpecifyKind(run.EndedAtUtc ?? DateTime.MinValue, DateTimeKind.Utc)),
                });
        }

        return reply;
    }

    /// <summary>
    /// Projects active phase progress without misrepresenting record counts as legacy site counts.
    /// </summary>
    internal async Task<StatusReply> StatusAsync(CancellationToken token = default)
    {
        StatusReply? reply = new StatusReply();
        ListReply? listed = await ListAsync(new ListRequest { Running = true }, token);
        foreach (ListScanResponse? row in listed.Status)
        {
            DateTime started = row.ScanStarted.ToDateTime();
            reply.Status.Add(
                new ScanStatusReply
                {
                    Id = row.Id,
                    Mode = row.Mode,
                    Status = row.Status,
                    ExecutionPath = row.ExecutionPath,
                    Phase = row.Phase,
                    SnapshotId = row.SnapshotId,
                    RunId = row.RunId,
                    AnalysisRunId = row.AnalysisRunId,
                    RecordsCompleted = row.RecordsCompleted,
                    RecordsTotal = row.RecordsTotal,
                    ErrorCount = row.ErrorCount,
                    ErrorSummary = row.ErrorSummary,
                    Started = row.ScanStarted,
                    Duration = Duration.FromTimeSpan(DateTime.UtcNow - started),
                });
        }

        return reply;
    }

    /// <summary>
    /// Creates queued phase metadata with fixed identities and versioned runtime settings.
    /// </summary>
    private static PhaseRunRow NewRun(
        Guid assessmentId,
        Guid id,
        Guid snapshotId,
        string key,
        string inputVersion,
        PhaseKind kind,
        int threads,
        VersionedJson parameters,
        VersionedJson? analysisParameters) =>
        new()
        {
            AssessmentId = assessmentId,
            RunId = id,
            SnapshotId = snapshotId,
            Kind = kind,
            CurrentPhase = kind,
            ModuleKey = key,
            InputVersion = inputVersion,
            Status = ScanStatus.Queued,
            ParametersJson =
                new PipelineRunSettings(threads, parameters.Json, (analysisParameters ?? VersionedJson.Empty).Json)
                    .Serialize(),
            CreatedAtUtc = DateTime.UtcNow,
        };

    /// <summary>
    /// Returns the immutable identities and rule selection acknowledged when a root is queued.
    /// </summary>
    private static PhaseReply Ticket(PhaseRunRow root) =>
        new()
        {
            AssessmentId = root.AssessmentId.ToString(),
            SnapshotId = root.SnapshotId.ToString(),
            RunId = root.RunId.ToString(),
            AnalysisRunId = PipelineRunSettings.Read(root).AnalysisRunId?.ToString() ?? "",
            Status = ScanStatus.Queued.ToString(),
            RuleVersion = root.RuleVersion ?? "",
        };

    /// <summary>
    /// Parses supplied versioned parameters or the explicit empty version-one envelope.
    /// </summary>
    private static VersionedJson ParseParameters(string? json) =>
        string.IsNullOrWhiteSpace(json) ? VersionedJson.Empty : new(json);

    /// <summary>
    /// Requires a nonempty native GUID for the selected pipeline identity.
    /// </summary>
    private static Guid ParseId(string value, string name) =>
        Guid.TryParse(value, out Guid id) && id != Guid.Empty
            ? id
            : throw new ArgumentException($"A valid {name} ID is required.");

    /// <summary>
    /// Rejects concurrency outside the supported bounded range.
    /// </summary>
    private static void ValidateThreads(int threads)
    {
        if (threads is < 1 or > 512) throw new ArgumentException("Threads must be between 1 and 512.");
    }

    /// <summary>
    /// Tracks one root run's cancellation, lifecycle serialization, settlement, and completion.
    /// </summary>
    private sealed class ActiveExecution(PhaseRunRow root)
    {
        internal PhaseRunRow Root { get; } = root;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal SemaphoreSlim Control { get; } = new(1, 1);

        internal TaskCompletionSource<ScanStatus> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ScanStatus? RequestedStatus { get; set; }
        internal bool Settled { get; set; }
    }
}