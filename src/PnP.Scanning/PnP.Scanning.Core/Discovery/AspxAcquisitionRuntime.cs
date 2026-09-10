using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal interface IAspxReferenceAcquisitionProvider
{
    AspxReferenceCollector ReferenceCollector { get; }
}

internal sealed record AspxAcquisitionRuntimeOptions(
    string PhysicalDatabasePath,
    string PhysicalOutputPath,
    string ReferenceDatabasePath,
    string ReferenceOutputPath,
    string AggregateOutputPath,
    DiscoveryRunManifest PhysicalManifest,
    string ScopeMode,
    bool FixtureRun,
    bool TenantVisibilityVerified,
    string PermissionBoundaryHash,
    string PlatformBuildRef,
    string SnapshotFence,
    AspxPlatformRegistryV1 Registry,
    Guid? ResumeRunId = null);

internal sealed record AspxAcquisitionRunResult(
    AspxDiscoveryOutputV2 Physical,
    AspxReferenceOutputV1 Reference,
    AspxAcquisitionVerdictV1 Aggregate);

internal sealed class AspxAcquisitionRuntime
{
    internal async Task<AspxAcquisitionRunResult> RunAsync(IAspxDiscoveryProvider provider,
        AspxAcquisitionRuntimeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        if (provider is not IAspxReferenceAcquisitionProvider referenceProvider)
            throw new InvalidOperationException("The acquisition provider must expose the reference companion volume.");
        ValidateDistinctPaths(options);
        var runId = options.ResumeRunId ?? Guid.NewGuid();
        var physical = await new AspxInventoryRuntime().RunAsync(provider,
            new(options.PhysicalDatabasePath, options.PhysicalOutputPath, options.PhysicalManifest,
                options.ScopeMode, options.FixtureRun, options.ResumeRunId, options.TenantVisibilityVerified,
                options.ResumeRunId == null ? runId : null), cancellationToken);

        var referenceManifest = new AspxReferenceRunManifest(
            AspxAcquisitionVersions.ReferenceProducer, AspxAcquisitionVersions.ReferenceStore,
            options.PhysicalManifest.ProductRef, options.PhysicalManifest.SdkRef,
            options.PhysicalManifest.ScopePolicyHash, options.PermissionBoundaryHash,
            options.Registry.RegistryRevision, options.Registry.RegistryHash, options.PlatformBuildRef,
            options.SnapshotFence, AspxAcquisitionVersions.LiveProvider, runId.ToString("D"));
        var reference = referenceProvider.ReferenceCollector.Build(runId, referenceManifest, physical, options.Registry);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReferenceDatabasePath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReferenceOutputPath))!);
        using (var store = new AspxReferenceStore(options.ReferenceDatabasePath))
            store.Write(referenceManifest, reference, options.ResumeRunId != null);
        await WriteJsonAtomicAsync(options.ReferenceOutputPath, reference, cancellationToken);

        var physicalFile = await AspxAggregateEvaluator.HashFileAsync(options.PhysicalOutputPath, cancellationToken);
        var referenceFile = await AspxAggregateEvaluator.HashFileAsync(options.ReferenceOutputPath, cancellationToken);
        var aggregateVerdict = AspxAggregateEvaluator.Evaluate(physical, reference, referenceManifest,
            options.Registry, out var aggregateGaps);
        var physicalBinding = new AspxVolumeBinding(physical.OutputVersion, physical.RunId,
            physicalFile.Hash, physicalFile.Length, options.PhysicalManifest.ProductRef,
            options.PhysicalManifest.ScopePolicyHash, options.SnapshotFence);
        var referenceBinding = new AspxVolumeBinding(reference.OutputVersion, reference.AcquisitionRunId,
            referenceFile.Hash, referenceFile.Length, options.PhysicalManifest.ProductRef,
            options.PhysicalManifest.ScopePolicyHash, options.SnapshotFence);
        var aggregate = new AspxAcquisitionVerdictV1(AspxAcquisitionVerdictV1.Version, runId,
            aggregateVerdict, physicalBinding, referenceBinding, AspxAcquisitionVersions.SurfaceContract,
            options.Registry.RegistryRevision, options.Registry.RegistryHash, options.PlatformBuildRef,
            options.PhysicalManifest.ProductRef, options.PhysicalManifest.SdkRef, DateTimeOffset.UtcNow,
            aggregateGaps);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.AggregateOutputPath))!);
        await WriteJsonAtomicAsync(options.AggregateOutputPath, aggregate, cancellationToken);
        return new(physical, reference, aggregate);
    }

    private static void ValidateDistinctPaths(AspxAcquisitionRuntimeOptions options)
    {
        var paths = new[]
        {
            options.PhysicalDatabasePath, options.PhysicalOutputPath, options.ReferenceDatabasePath,
            options.ReferenceOutputPath, options.AggregateOutputPath,
        }.Select(Path.GetFullPath).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new ArgumentException("Physical, reference and aggregate paths must be distinct.", nameof(options));
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(value, AspxInventoryRuntime.JsonOptions(indented: true));
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
