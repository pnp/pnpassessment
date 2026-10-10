#nullable enable
using System.Collections.Concurrent;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using PnP.Scanning.Core.Pipeline.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;

namespace PnP.Scanning.Core.Pipeline.Orchestration;

/// <summary>Shared stage scheduling and lifecycle. Legacy web cleanup and auth restart are never used here.</summary>
internal sealed class PipelineCoordinator(PipelineStore store, ModuleRegistry registry,
    ICollectionEnvironment collectionEnvironment) : IHostedService
{
    private readonly SemaphoreSlim scheduling = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ActiveExecution> active = new();
    private bool stopping;

    internal int ActiveCount => active.Count;
    internal bool IsActive(Guid assessmentId) => active.ContainsKey(assessmentId);

    public Task StartAsync(CancellationToken cancellationToken) => store.MarkInterruptedRunsAsync(cancellationToken);
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping = true;
        foreach (var execution in active.Values.ToArray())
            await RequestStopAsync(execution, ScanStatus.Terminated, cancellationToken);
    }

    internal Task<PhaseReply> CollectAsync(CollectRequest request, CancellationToken token = default) =>
        QueueCollectionAsync(request, null, null, token);

    internal Task<PhaseReply> StartPipelineAsync(StartPipelineRequest request, CancellationToken token = default) =>
        QueueCollectionAsync(request.Collection ?? throw new ArgumentException("A collection request is required."),
            request.RuleVersion, ParseParameters(request.AnalysisParametersJson), token);

    private async Task<PhaseReply> QueueCollectionAsync(CollectRequest request, string? ruleVersion,
        VersionedJson? analysisParameters, CancellationToken token)
    {
        // Metadata validation occurs before database creation, factories, credentials or online services.
        var collector = registry.GetCollector(request.Module);
        var parameters = ParseParameters(request.ParametersJson);
        collector.ValidateParameters?.Invoke(parameters);
        var analyzer = analysisParameters == null ? null : registry.GetAnalyzer(request.Module, ruleVersion, collector.InputVersion);
        if (analyzer?.PinParameters != null) analysisParameters = analyzer.PinParameters(analysisParameters!);
        analyzer?.ValidateParameters?.Invoke(analysisParameters!);
        var options = request.CollectionOptions?.Clone() ?? throw new ArgumentException("Collection options are required.");
        collector.ValidateOptions?.Invoke(options);
        ValidateThreads(options.Threads);

        await scheduling.WaitAsync(token);
        try
        {
            CheckCapacity();
            var assessmentId = Guid.NewGuid();
            var snapshotId = Guid.NewGuid();
            var rootId = Guid.NewGuid();
            var analysisId = analyzer == null ? (Guid?)null : Guid.NewGuid();
            var protectedOptions = collectionEnvironment.Protect(options);
            var root = NewRun(assessmentId, rootId, snapshotId, collector.ModuleKey, collector.InputVersion,
                analyzer == null ? PhaseKind.Collection : PhaseKind.Pipeline, options.Threads, parameters, analysisParameters);
            root.CurrentPhase = PhaseKind.Collection;
            root.RuleVersion = analyzer?.RuleVersion;
            root.AnalysisRunId = analysisId;
            root.CollectionOptionsJson = VersionedJson.From(JsonFormatter.Default.Format(protectedOptions)).Json;
            PhaseRunRow? child = null;
            if (analyzer != null)
            {
                child = NewRun(assessmentId, Guid.NewGuid(), snapshotId, collector.ModuleKey, collector.InputVersion,
                    PhaseKind.Collection, options.Threads, parameters, analysisParameters);
                child.ParentRunId = rootId;
            }
            var scope = VersionedJson.From(new
            {
                options.Tenant, options.Environment, options.SitesList, options.SitesFile,
                options.AdminCenterUrl, options.MySiteHostUrl,
                properties = options.Properties.OrderBy(x => x.Property).Select(x => new { x.Property, x.Type, x.Value }).ToArray(),
                moduleParameters = parameters.Value,
            });
            await store.CreateCollectionAsync(new SourceSnapshotRow
            {
                AssessmentId = assessmentId, SnapshotId = snapshotId, ModuleKey = collector.ModuleKey,
                InputVersion = collector.InputVersion, ScopeJson = scope.Json, CreatedAtUtc = DateTime.UtcNow,
            }, root, child, protectedOptions, token);
            Launch(root);
            return Ticket(root);
        }
        finally { scheduling.Release(); }
    }

    internal async Task<PhaseReply> AnalyzeAsync(AnalyzeRequest request, CancellationToken token = default)
    {
        var assessmentId = ParseId(request.Id, "assessment");
        var snapshotId = ParseId(request.SnapshotId, "snapshot");
        var snapshot = await store.OpenSnapshotAsync(assessmentId, snapshotId, token);
        var analyzer = registry.GetAnalyzer(snapshot.ModuleKey, request.RuleVersion, snapshot.InputVersion);
        var parameters = ParseParameters(request.ParametersJson);
        if (analyzer.PinParameters != null) parameters = analyzer.PinParameters(parameters);
        analyzer.ValidateParameters?.Invoke(parameters);
        var threads = request.Threads == 0 ? (await store.LatestRootAsync(assessmentId, token))?.Threads ?? 1 : request.Threads;
        ValidateThreads(threads);

        await scheduling.WaitAsync(token);
        try
        {
            CheckCapacity(assessmentId);
            var id = Guid.NewGuid();
            var root = NewRun(assessmentId, id, snapshotId, snapshot.ModuleKey, snapshot.InputVersion,
                PhaseKind.Analysis, threads, parameters, parameters);
            root.AnalysisRunId = id;
            root.RuleVersion = analyzer.RuleVersion;
            root.TotalRecords = snapshot.ObservationIds.Count;
            await store.CreateAnalysisAsync(root, token);
            Launch(root);
            return Ticket(root);
        }
        finally { scheduling.Release(); }
    }

    internal async Task<PhaseReply> RestartAsync(Guid assessmentId, int threads = 0,
        Guid? runId = null, CancellationToken token = default)
    {
        if (threads != 0) ValidateThreads(threads);
        await scheduling.WaitAsync(token);
        try
        {
            CheckCapacity(assessmentId);
            var runs = await store.GetRunsAsync(assessmentId, token);
            var root = runId == null ? runs.LastOrDefault(x => x.ParentRunId == null) :
                runs.SingleOrDefault(x => x.RunId == runId && x.ParentRunId == null);
            if (root == null) throw new NotSupportedException("This assessment has no such pipeline run; use the legacy restart for legacy assessments.");
            if (root.Status is not (ScanStatus.Paused or ScanStatus.Terminated))
                throw new InvalidOperationException($"Cannot restart a run whose status is {root.Status}.");
            // Never substitute the current rule for the one recorded on the original run.
            if (root.Kind != PhaseKind.Collection)
            {
                var analyzer = registry.GetAnalyzer(root.ModuleKey, root.RuleVersion, root.InputVersion);
                analyzer.ValidateParameters?.Invoke(new VersionedJson(root.AnalysisParametersJson));
            }
            if (root.CurrentPhase == PhaseKind.Collection)
            {
                var collector = registry.GetCollector(root.ModuleKey, root.InputVersion);
                collector.ValidateParameters?.Invoke(new VersionedJson(root.ParametersJson));
            }
            else await store.OpenSnapshotAsync(assessmentId, root.SnapshotId, token);

            await store.QueueRestartAsync(assessmentId, root.RunId, threads, token);
            if (threads > 0) root.Threads = threads;
            Launch(root);
            return Ticket(root);
        }
        finally { scheduling.Release(); }
    }

    internal async Task PauseAsync(Guid assessmentId, bool all, CancellationToken token = default)
    {
        var selected = all ? active.Values.ToArray() : active.TryGetValue(assessmentId, out var execution)
            ? [execution] : throw new InvalidOperationException("The selected pipeline assessment is not running.");
        foreach (var item in selected) await RequestStopAsync(item, ScanStatus.Paused, token);
    }

    internal async Task TerminateAsync(Guid assessmentId, CancellationToken token = default)
    {
        if (!active.TryGetValue(assessmentId, out var execution))
            throw new InvalidOperationException("The selected pipeline assessment is not running.");
        await RequestStopAsync(execution, ScanStatus.Terminated, token);
    }

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
        finally { execution.Control.Release(); }
        try { await execution.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30), token); }
        catch (TimeoutException)
        {
            await execution.Control.WaitAsync(token);
            try
            {
                if (execution.Settled) return;
                execution.RequestedStatus = ScanStatus.Terminated;
                await store.SettleRootAsync(execution.Root.AssessmentId, execution.Root.RunId, ScanStatus.Terminated,
                    "A module did not settle cancellation within 30 seconds.", token);
            }
            finally { execution.Control.Release(); }
            throw;
        }
    }

    private void CheckCapacity(Guid? assessmentId = null)
    {
        if (stopping) throw new InvalidOperationException("Assessment engine is stopping.");
        if (assessmentId != null && active.ContainsKey(assessmentId.Value))
            throw new InvalidOperationException("An assessment already has an active phase; pause or finish it before starting another run.");
        if (active.Count + ScanManager.LegacyScansRunning >= ScanManager.MaxParallelScans)
            throw new InvalidOperationException("Max number of parallel assessments reached.");
    }

    private void Launch(PhaseRunRow root)
    {
        var execution = new ActiveExecution(root);
        if (!active.TryAdd(root.AssessmentId, execution)) throw new InvalidOperationException("Assessment already has an active phase.");
        _ = Task.Run(() => ExecuteAsync(execution));
    }

    private async Task ExecuteAsync(ActiveExecution execution)
    {
        var root = execution.Root;
        var token = execution.Cancellation.Token;
        Exception? failure = null;
        try
        {
            var runs = await store.GetRunsAsync(root.AssessmentId, token);
            root = runs.Single(x => x.RunId == root.RunId);
            if (root.CurrentPhase == PhaseKind.Collection)
            {
                var phase = root.Kind == PhaseKind.Collection ? root : runs.Single(x => x.ParentRunId == root.RunId && x.Kind == PhaseKind.Collection);
                var registration = registry.GetCollector(root.ModuleKey, root.InputVersion);
                var module = registration.Create();
                if (!module.ModuleKey.Equals(registration.ModuleKey, StringComparison.OrdinalIgnoreCase) || module.InputVersion != registration.InputVersion)
                    throw new InvalidOperationException("Collection implementation differs from its registered contract.");
                await store.SetRunningAsync(root.AssessmentId, root.RunId, phase.RunId, token);
                var savedOptions = new VersionedJson(root.CollectionOptionsJson!).Value.GetString()!;
                var options = collectionEnvironment.Restore(JsonParser.Default.Parse<StartRequest>(savedOptions));
                options.Threads = root.Threads;
                var writer = store.SnapshotWriter(root.AssessmentId, root.SnapshotId, phase.RunId);
                var journal = store.CollectionJournal(root.AssessmentId, root.SnapshotId, phase.RunId);
                var context = new CollectionContext(options, new VersionedJson(root.ParametersJson),
                    phase.CheckpointJson == null ? null : new VersionedJson(phase.CheckpointJson), collectionEnvironment,
                    root.AssessmentId, journal);
                await foreach (var record in module.CollectAsync(context, token).WithCancellation(token))
                    await writer.CommitAsync(record, token);
                token.ThrowIfCancellationRequested();
                if (module is ICollectionSnapshotValidator validator)
                    await validator.ValidateAsync(journal, token);
                await writer.SealAsync(token);
            }
            if (root.Kind != PhaseKind.Collection)
            {
                token.ThrowIfCancellationRequested();
                // After a sealed checkpoint this path never resolves a collector or restores credentials.
                var registration = registry.GetAnalyzer(root.ModuleKey, root.RuleVersion, root.InputVersion);
                var module = registration.Create();
                if (!module.ModuleKey.Equals(registration.ModuleKey, StringComparison.OrdinalIgnoreCase) || module.RuleVersion != registration.RuleVersion)
                    throw new InvalidOperationException("Analysis implementation differs from its registered contract.");
                var phase = await store.PrepareAnalysisChildAsync(root, token);
                if (phase.Status != ScanStatus.Finished)
                {
                await store.SetRunningAsync(root.AssessmentId, root.RunId, phase.RunId, token);
                await new AnalysisExecutor().ExecuteAsync(module, store.SnapshotReader(root.AssessmentId, root.SnapshotId),
                    store.ResultWriter(root.AssessmentId, phase.RunId), new VersionedJson(root.AnalysisParametersJson), root.Threads, token);
                if (module is IAnalysisFinalizer finalizer)
                    await finalizer.FinalizeAsync(store.SnapshotReader(root.AssessmentId, root.SnapshotId),
                        store.ResultReader(root.AssessmentId, phase.RunId), store.ReportWriter(root.AssessmentId, phase.RunId),
                        new VersionedJson(root.AnalysisParametersJson), token);
                await store.FinishAnalysisAsync(root.AssessmentId, phase.RunId, token);
                }
                else await store.OpenSnapshotAsync(root.AssessmentId, root.SnapshotId, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }

        await execution.Control.WaitAsync();
        try
        {
            var status = execution.RequestedStatus ?? (failure == null ? ScanStatus.Finished : ScanStatus.Terminated);
            var error = failure?.ToString() ?? (status == ScanStatus.Terminated ? "Execution was canceled by stop." : null);
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

    internal async Task<ScanStatus> WaitForCompletionAsync(Guid assessmentId, CancellationToken token = default)
    {
        if (active.TryGetValue(assessmentId, out var execution)) return await execution.Completion.Task.WaitAsync(token);
        return (await store.LatestRootAsync(assessmentId, token))?.Status ?? throw new KeyNotFoundException("Assessment is unknown.");
    }

    internal async Task<bool> OwnsAssessmentAsync(Guid assessmentId, CancellationToken token = default)
    {
        if (!File.Exists(store.DatabasePath(assessmentId))) return false;
        return (await store.GetRunsAsync(assessmentId, token)).Count > 0;
    }

    internal async Task<ListReply> ListAsync(ListRequest request, CancellationToken token = default)
    {
        var all = !request.Running && !request.Paused && !request.Finished && !request.Terminated;
        var reply = new ListReply();
        foreach (var run in await store.ListRootsAsync(token))
        {
            if (!(all || request.Running && run.Status is ScanStatus.Running or ScanStatus.Queued ||
                request.Paused && run.Status is ScanStatus.Paused or ScanStatus.Pausing ||
                request.Finished && run.Status == ScanStatus.Finished || request.Terminated && run.Status == ScanStatus.Terminated)) continue;
            reply.Status.Add(new ListScanResponse
            {
                Id = run.AssessmentId.ToString(), Mode = run.ModuleKey, Status = run.Status.ToString(), ExecutionPath = "pipeline",
                Phase = run.CurrentPhase.ToString(), SnapshotId = run.SnapshotId.ToString(), RunId = run.RunId.ToString(),
                AnalysisRunId = run.AnalysisRunId?.ToString() ?? "", RecordsCompleted = run.CompletedRecords,
                RecordsTotal = run.TotalRecords, ErrorCount = run.ErrorCount, ErrorSummary = run.LastError ?? "",
                // SQLite stores the UTC value but does not preserve DateTime.Kind on materialization.
                ScanStarted = Timestamp.FromDateTime(DateTime.SpecifyKind(run.CreatedAtUtc, DateTimeKind.Utc)),
                ScanEnded = Timestamp.FromDateTime(DateTime.SpecifyKind(run.EndedAtUtc ?? DateTime.MinValue, DateTimeKind.Utc)),
            });
        }
        return reply;
    }

    internal async Task<StatusReply> StatusAsync(CancellationToken token = default)
    {
        var reply = new StatusReply();
        var listed = await ListAsync(new ListRequest { Running = true }, token);
        foreach (var row in listed.Status)
        {
            var started = row.ScanStarted.ToDateTime();
            reply.Status.Add(new ScanStatusReply
            {
                Id = row.Id, Mode = row.Mode, Status = row.Status, ExecutionPath = row.ExecutionPath, Phase = row.Phase,
                SnapshotId = row.SnapshotId, RunId = row.RunId, AnalysisRunId = row.AnalysisRunId,
                RecordsCompleted = row.RecordsCompleted, RecordsTotal = row.RecordsTotal,
                ErrorCount = row.ErrorCount, ErrorSummary = row.ErrorSummary, Started = row.ScanStarted,
                Duration = Duration.FromTimeSpan(DateTime.UtcNow - started),
            });
        }
        return reply;
    }

    private static PhaseRunRow NewRun(Guid assessmentId, Guid id, Guid snapshotId, string key, string inputVersion,
        PhaseKind kind, int threads, VersionedJson parameters, VersionedJson? analysisParameters) => new()
    {
        AssessmentId = assessmentId, RunId = id, SnapshotId = snapshotId, Kind = kind, CurrentPhase = kind,
        ModuleKey = key, InputVersion = inputVersion, Status = ScanStatus.Queued, Threads = threads,
        ParametersJson = parameters.Json, AnalysisParametersJson = (analysisParameters ?? VersionedJson.Empty).Json,
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static PhaseReply Ticket(PhaseRunRow root) => new()
    {
        AssessmentId = root.AssessmentId.ToString(), SnapshotId = root.SnapshotId.ToString(), RunId = root.RunId.ToString(),
        AnalysisRunId = root.AnalysisRunId?.ToString() ?? "", Status = ScanStatus.Queued.ToString(), RuleVersion = root.RuleVersion ?? "",
    };

    private static VersionedJson ParseParameters(string? json) => string.IsNullOrWhiteSpace(json) ? VersionedJson.Empty : new(json);
    private static Guid ParseId(string value, string name) => Guid.TryParse(value, out var id) && id != Guid.Empty
        ? id : throw new ArgumentException($"A valid {name} ID is required.");
    private static void ValidateThreads(int threads)
    {
        if (threads is < 1 or > 512) throw new ArgumentException("Threads must be between 1 and 512.");
    }

    private sealed class ActiveExecution(PhaseRunRow root)
    {
        internal PhaseRunRow Root { get; } = root;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal SemaphoreSlim Control { get; } = new(1, 1);
        internal TaskCompletionSource<ScanStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ScanStatus? RequestedStatus { get; set; }
        internal bool Settled { get; set; }
    }
}
