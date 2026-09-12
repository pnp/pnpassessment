using System.Text.Json;
using System.Text.Json.Serialization;

namespace PnP.Scanning.Core.Discovery;

internal sealed record DiscoveryChildEnumerationResult(
    DiscoveryScopeKind ChildKind,
    IReadOnlyList<DiscoveryChildExpectation> ExpectedChildren,
    IReadOnlyList<DiscoveryScopeRegistration> ObservedChildren,
    DiscoveryTerminalOutcome Outcome,
    string PermissionContext,
    string GapCode = null,
    string GapDetail = null);

internal interface IAspxDiscoveryProvider : IDisposable
{
    DiscoveryScopeRegistration RootScope { get; }
    Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(
        DiscoveryScopeRegistration parent, CancellationToken cancellationToken = default);
    IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface);
}

internal sealed class AspxDiscoveryOrchestrator
{
    private readonly DiscoveryStore store;

    internal AspxDiscoveryOrchestrator(DiscoveryStore store) => this.store = store;

    internal async Task RunAsync(Guid runId, IAspxDiscoveryProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var pending = new Queue<DiscoveryScopeRegistration>();
        store.RegisterScope(runId, provider.RootScope);
        pending.Enqueue(provider.RootScope);

        while (pending.TryDequeue(out var scope))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scope.SourceKind != null)
            {
                var source = provider.CreateRawSource(scope);
                if (source == null)
                {
                    store.RecordGap(runId, scope.ScopeKey, DiscoveryGapCodes.SourceUnsupported,
                        $"No raw source was available for required {scope.Kind} surface '{scope.Locator}'.");
                    store.RecordScopeOutcome(runId, scope.ScopeKey, DiscoveryTerminalOutcome.Unknown);
                }
                else
                {
                    await new AspxDiscoveryRunner(store).RunSurfaceAsync(runId, scope, source, cancellationToken);
                }
            }

            DiscoveryChildEnumerationResult result;
            try
            {
                result = await provider.EnumerateChildrenAsync(scope, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new(ChildKindFor(scope.Kind), Array.Empty<DiscoveryChildExpectation>(),
                    Array.Empty<DiscoveryScopeRegistration>(), DiscoveryTerminalOutcome.Failed,
                    scope.PermissionContext, GapDetail: $"{ex.GetType().Name}: {ex.Message}");
            }

            store.RecordChildEnumeration(runId, scope.ScopeKey, result.ChildKind, result.ExpectedChildren,
                result.Outcome, result.PermissionContext, result.GapCode, result.GapDetail);
            foreach (var child in result.ObservedChildren)
            {
                store.RegisterScope(runId, child);
                pending.Enqueue(child);
            }

            if (scope.SourceKind == null || result.Outcome is not (DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty))
                store.RecordScopeOutcome(runId, scope.ScopeKey, result.Outcome);
        }
    }

    internal static DiscoveryScopeKind ChildKindFor(DiscoveryScopeKind parentKind) => parentKind switch
    {
        DiscoveryScopeKind.Tenant => DiscoveryScopeKind.Geo,
        DiscoveryScopeKind.Geo => DiscoveryScopeKind.SiteCollection,
        DiscoveryScopeKind.SiteCollection => DiscoveryScopeKind.Web,
        DiscoveryScopeKind.Web => DiscoveryScopeKind.Container,
        DiscoveryScopeKind.Container => DiscoveryScopeKind.Folder,
        DiscoveryScopeKind.Folder => DiscoveryScopeKind.Folder,
        _ => throw new ArgumentOutOfRangeException(nameof(parentKind), parentKind, null),
    };
}

internal sealed record AspxInventoryRuntimeOptions(
    string DatabasePath,
    string OutputPath,
    DiscoveryRunManifest Manifest,
    string ScopeMode,
    bool FixtureRun,
    Guid? ResumeRunId = null,
    bool TenantVisibilityVerified = false,
    Guid? NewRunId = null);

internal sealed class AspxInventoryRuntime
{
    internal async Task<AspxDiscoveryOutputV2> RunAsync(IAspxDiscoveryProvider provider,
        AspxInventoryRuntimeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        if (!AspxScopeModes.IsKnown(options.ScopeMode))
            throw new ArgumentException(
                $"scopeMode must be '{AspxScopeModes.ProductTenantAuthority}', '{AspxScopeModes.TenantFull}' or '{AspxScopeModes.DeclaredSubset}'.",
                nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);

        if (options.ResumeRunId != null && options.NewRunId != null)
            throw new ArgumentException("ResumeRunId and NewRunId are mutually exclusive.", nameof(options));
        var runId = options.ResumeRunId ?? options.NewRunId ?? Guid.NewGuid();
        using var store = new DiscoveryStore(options.DatabasePath);
        if (options.ResumeRunId == null)
        {
            store.CreateRun(runId, options.Manifest, options.ScopeMode, options.FixtureRun);
        }
        else
        {
            if (!store.RunExists(runId))
                throw new InvalidOperationException($"Cannot resume unknown discovery run '{runId:D}'.");
            var decision = ResumeDecision.Evaluate(store.ReadManifest(runId), options.Manifest, options.FixtureRun);
            if (!decision.CanResume)
                throw new InvalidOperationException("Immutable discovery provenance drift: " + string.Join(", ", decision.MismatchFields));
            if (!string.Equals(store.ReadScopeMode(runId), options.ScopeMode, StringComparison.Ordinal) ||
                store.ReadFixtureRun(runId) != options.FixtureRun)
                throw new InvalidOperationException("Resume scopeMode/fixtureRun does not match the persisted run.");
            store.ResumeExecution(runId);
        }

        try
        {
            await new AspxDiscoveryOrchestrator(store).RunAsync(runId, provider, cancellationToken);
            store.FinishExecution(runId, DiscoveryExecutionStatus.Finished);
        }
        catch (OperationCanceledException)
        {
            store.FinishExecution(runId, DiscoveryExecutionStatus.Cancelled);
            throw;
        }
        catch
        {
            store.FinishExecution(runId, DiscoveryExecutionStatus.Failed);
            throw;
        }

        var verdict = store.EvaluateVerdict(runId, options.TenantVisibilityVerified);
        var output = AspxDiscoveryOutputV2.Create(runId, verdict, store);
        await WriteOutputAsync(options.OutputPath, output, cancellationToken);
        return output;
    }

    private static async Task WriteOutputAsync(string outputPath, AspxDiscoveryOutputV2 output,
        CancellationToken cancellationToken)
    {
        var temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(output, JsonOptions(indented: true));
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, outputPath, overwrite: true);
    }

    internal static JsonSerializerOptions JsonOptions(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal sealed record AspxDiscoveryInputV1(
    string Version,
    string RootScopeKey,
    string ScopeMode,
    bool FixtureRun,
    bool TenantVisibilityVerified,
    IReadOnlyList<AspxDiscoveryInputScope> Scopes)
{
    internal const string CurrentVersion = "aspx-discovery-input/v1";
}

internal sealed record AspxDiscoveryInputScope(
    string ScopeKey,
    string ParentScopeKey,
    DiscoveryScopeKind Kind,
    DiscoverySourceKind? SourceKind,
    string Locator,
    string PermissionContext,
    bool Required,
    DiscoveryTerminalOutcome EnumerationOutcome,
    IReadOnlyList<string> ExpectedChildScopeKeys,
    IReadOnlyList<RawDiscoveryBatch> Batches,
    string GapCode = null,
    string GapDetail = null);

/// <summary>
/// Non-test, deterministic hierarchy source used by the production CLI for sealed offline replay.
/// The same orchestration contract can be implemented by a live provider without changing the ledger.
/// </summary>
internal sealed class ManifestAspxDiscoveryProvider : IAspxDiscoveryProvider
{
    private readonly IReadOnlyDictionary<string, AspxDiscoveryInputScope> scopes;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AspxDiscoveryInputScope>> children;

    private ManifestAspxDiscoveryProvider(AspxDiscoveryInputV1 input)
    {
        Input = input;
        Validate(input);
        scopes = input.Scopes.ToDictionary(scope => scope.ScopeKey, StringComparer.Ordinal);
        children = input.Scopes.Where(scope => scope.ParentScopeKey != null)
            .GroupBy(scope => scope.ParentScopeKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AspxDiscoveryInputScope>)group
                .OrderBy(scope => scope.ScopeKey, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        RootScope = Registration(scopes[input.RootScopeKey]);
    }

    internal AspxDiscoveryInputV1 Input { get; }
    public DiscoveryScopeRegistration RootScope { get; }

    internal static async Task<ManifestAspxDiscoveryProvider> LoadAsync(string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var input = JsonSerializer.Deserialize<AspxDiscoveryInputV1>(json, AspxInventoryRuntime.JsonOptions())
            ?? throw new InvalidOperationException("Input JSON did not contain an ASPX discovery input document.");
        return new(input);
    }

    public Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(
        DiscoveryScopeRegistration parent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var definition = scopes[parent.ScopeKey];
        var childKind = AspxDiscoveryOrchestrator.ChildKindFor(parent.Kind);
        var expected = (definition.ExpectedChildScopeKeys ?? Array.Empty<string>()).Select(key =>
        {
            if (scopes.TryGetValue(key, out var child))
                return new DiscoveryChildExpectation(child.ScopeKey, child.Kind, child.SourceKind,
                    child.Locator, child.PermissionContext, child.Required);
            return new DiscoveryChildExpectation(key, childKind, null, null, definition.PermissionContext);
        }).ToArray();
        var observed = children.TryGetValue(parent.ScopeKey, out var found)
            ? found.Select(Registration).ToArray()
            : Array.Empty<DiscoveryScopeRegistration>();
        return Task.FromResult(new DiscoveryChildEnumerationResult(childKind, expected, observed,
            definition.EnumerationOutcome, definition.PermissionContext, definition.GapCode, definition.GapDetail));
    }

    public IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface)
    {
        var definition = scopes[surface.ScopeKey];
        return definition.SourceKind == null ? null : new ManifestRawDiscoverySource(definition.Batches);
    }

    public void Dispose() { }

    private static DiscoveryScopeRegistration Registration(AspxDiscoveryInputScope scope) => new(
        scope.ScopeKey, scope.ParentScopeKey, scope.Kind, scope.SourceKind, scope.Locator,
        scope.PermissionContext, scope.Required);

    private static void Validate(AspxDiscoveryInputV1 input)
    {
        if (input.Version != AspxDiscoveryInputV1.CurrentVersion)
            throw new InvalidOperationException($"Unsupported input version '{input.Version}'.");
        if (!AspxScopeModes.IsKnown(input.ScopeMode))
            throw new InvalidOperationException(
                $"Input scopeMode must be '{AspxScopeModes.ProductTenantAuthority}', '{AspxScopeModes.TenantFull}' or '{AspxScopeModes.DeclaredSubset}'.");
        if (input.Scopes == null || input.Scopes.Count == 0)
            throw new InvalidOperationException("Input must contain at least one scope.");
        var duplicate = input.Scopes.GroupBy(scope => scope.ScopeKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null) throw new InvalidOperationException($"Duplicate scope key '{duplicate.Key}'.");
        var map = input.Scopes.ToDictionary(scope => scope.ScopeKey, StringComparer.Ordinal);
        if (!map.TryGetValue(input.RootScopeKey, out var root) || root.ParentScopeKey != null ||
            root.Kind != DiscoveryScopeKind.Tenant)
            throw new InvalidOperationException("rootScopeKey must identify the parentless Tenant scope.");

        foreach (var scope in input.Scopes)
        {
            if (string.IsNullOrWhiteSpace(scope.ScopeKey) || string.IsNullOrWhiteSpace(scope.Locator) ||
                string.IsNullOrWhiteSpace(scope.PermissionContext))
                throw new InvalidOperationException("Every scope requires scopeKey, locator and permissionContext.");
            if (scope.ParentScopeKey != null && !map.ContainsKey(scope.ParentScopeKey))
                throw new InvalidOperationException($"Scope '{scope.ScopeKey}' has unknown parent '{scope.ParentScopeKey}'.");
            var expectedKind = AspxDiscoveryOrchestrator.ChildKindFor(scope.Kind);
            foreach (var expectedKey in scope.ExpectedChildScopeKeys ?? Array.Empty<string>())
                if (map.TryGetValue(expectedKey, out var expectedScope) && expectedScope.Kind != expectedKind)
                    throw new InvalidOperationException($"Expected child '{expectedKey}' has kind {expectedScope.Kind}, expected {expectedKind}.");
            var observedKeys = input.Scopes.Where(candidate => candidate.ParentScopeKey == scope.ScopeKey)
                .Select(candidate => candidate.ScopeKey).ToHashSet(StringComparer.Ordinal);
            var expectedKeys = (scope.ExpectedChildScopeKeys ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
            if (observedKeys.Except(expectedKeys, StringComparer.Ordinal).FirstOrDefault() is { } unexpected)
                throw new InvalidOperationException($"Observed child '{unexpected}' is absent from '{scope.ScopeKey}' denominator.");
            if (scope.SourceKind != null && (scope.Batches == null || scope.Batches.Count == 0))
                throw new InvalidOperationException($"Raw surface '{scope.ScopeKey}' requires at least one explicit batch.");
        }
    }
}

internal sealed class ManifestRawDiscoverySource : IRawDiscoverySource
{
    private readonly IReadOnlyList<RawDiscoveryBatch> batches;

    internal ManifestRawDiscoverySource(IReadOnlyList<RawDiscoveryBatch> batches) =>
        this.batches = batches ?? throw new ArgumentNullException(nameof(batches));

    public async IAsyncEnumerable<RawDiscoveryBatch> ReadBatchesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var batch in batches.OrderBy(batch => batch.BatchOrdinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return batch;
            await Task.Yield();
        }
    }
}
