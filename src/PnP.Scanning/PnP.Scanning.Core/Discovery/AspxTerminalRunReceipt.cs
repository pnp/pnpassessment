using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal static class AspxTerminalVolumeRoles
{
    internal const string PhysicalDatabase = "physical-database";
    internal const string PhysicalOutput = "physical-output";
    internal const string ReferenceDatabase = "reference-database";
    internal const string ReferenceOutput = "reference-output";
    internal const string AggregateOutput = "aggregate-output";

    internal static readonly IReadOnlyList<string> Required = new[]
    {
        PhysicalDatabase, PhysicalOutput, ReferenceDatabase, ReferenceOutput, AggregateOutput,
    };

    internal static readonly IReadOnlyDictionary<string, string> ExpectedVersions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PhysicalDatabase] = DiscoveryRunManifest.CurrentSchemaVersion,
            [PhysicalOutput] = AspxDiscoveryOutputV2.Version,
            [ReferenceDatabase] = AspxAcquisitionVersions.ReferenceStore,
            [ReferenceOutput] = AspxReferenceOutputV2.Version,
            [AggregateOutput] = AspxAcquisitionVerdictV2.Version,
        };
}

internal sealed record AspxTerminalOutputSpec(string Role, string OutputVersion, string Path);

internal sealed record AspxTerminalVolumeBinding(
    string Role,
    string OutputVersion,
    string FileName,
    string Sha256,
    long Length);

internal sealed record AspxManagedExecutableBinding(
    string AssemblyName,
    string PackageVersion,
    string InformationalVersion,
    string ExecutableFileName,
    string ExecutableSha256,
    long? ExecutableLength,
    string DependencyGraphSha256,
    int DependencyGraphEntryCount,
    string DependencyManifestFileName,
    string DependencyManifestSha256,
    long? DependencyManifestLength)
{
    internal static async Task<AspxManagedExecutableBinding> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var name = assembly.GetName();
        var location = string.IsNullOrWhiteSpace(assembly.Location) ? Environment.ProcessPath : assembly.Location;
        string executableHash = null;
        long? executableLength = null;
        if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
        {
            var value = await AspxAggregateEvaluator.HashFileAsync(location, cancellationToken);
            executableHash = value.Hash;
            executableLength = value.Length;
        }
        var references = assembly.GetReferencedAssemblies()
            .Select(reference => $"{reference.Name}|{reference.Version}|{reference.CultureName}|{PublicKeyTokenHex(reference)}")
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var dependencyGraphHash = DiscoveryHash.Of(string.Join("\n", references));
        var depsPath = string.IsNullOrWhiteSpace(location) ? null : Path.ChangeExtension(location, ".deps.json");
        string depsHash = null;
        long? depsLength = null;
        if (!string.IsNullOrWhiteSpace(depsPath) && File.Exists(depsPath))
        {
            var value = await AspxAggregateEvaluator.HashFileAsync(depsPath, cancellationToken);
            depsHash = value.Hash;
            depsLength = value.Length;
        }
        return new(name.Name, name.Version?.ToString(),
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            string.IsNullOrWhiteSpace(location) ? null : Path.GetFileName(location), executableHash,
            executableLength, dependencyGraphHash, references.Length,
            string.IsNullOrWhiteSpace(depsPath) || !File.Exists(depsPath) ? null : Path.GetFileName(depsPath),
            depsHash, depsLength);
    }

    private static string PublicKeyTokenHex(AssemblyName name)
    {
        var token = name.GetPublicKeyToken();
        return token == null || token.Length == 0 ? string.Empty : Convert.ToHexString(token).ToLowerInvariant();
    }
}

internal sealed record AspxTerminalRunReceiptV1(
    string ReceiptVersion,
    Guid ArtifactRunId,
    int? ExitCode,
    string CompletionState,
    string ProductRef,
    string SdkRef,
    string SnapshotFence,
    string AggregateVerdict,
    AspxManagedExecutableBinding Executable,
    IReadOnlyList<AspxTerminalVolumeBinding> Volumes,
    DateTimeOffset CompletedAtUtc,
    string ErrorCode,
    string ErrorDigest)
{
    internal const string Version = AspxAcquisitionVersions.TerminalReceipt;
}

internal sealed record AspxTerminalReceiptValidation(bool Valid, IReadOnlyList<string> GapCodes);

internal static class AspxTerminalRunReceiptValidator
{
    internal static AspxTerminalReceiptValidation Validate(AspxTerminalRunReceiptV1 receipt)
    {
        var gaps = new HashSet<string>(StringComparer.Ordinal);
        if (receipt == null) return new(false, new[] { "terminal_receipt_missing" });
        if (receipt.ReceiptVersion != AspxTerminalRunReceiptV1.Version)
            gaps.Add("terminal_receipt_version_incompatible");
        if (receipt.ExitCode == null) gaps.Add("terminal_exit_code_missing");
        if (receipt.ArtifactRunId == Guid.Empty) gaps.Add("terminal_artifact_run_id_missing");
        if (receipt.CompletedAtUtc == default) gaps.Add("terminal_completed_utc_missing");
        ValidateExecutable(receipt.Executable, gaps);
        var volumes = receipt.Volumes ?? Array.Empty<AspxTerminalVolumeBinding>();
        foreach (var duplicate in volumes.GroupBy(volume => volume.Role, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            gaps.Add("terminal_volume_role_duplicate:" + duplicate.Key);
        foreach (var volume in volumes)
        {
            if (!AspxTerminalVolumeRoles.ExpectedVersions.TryGetValue(volume.Role, out var expectedVersion))
                gaps.Add("terminal_volume_role_unknown:" + volume.Role);
            else if (!string.Equals(volume.OutputVersion, expectedVersion, StringComparison.Ordinal))
                gaps.Add("terminal_volume_version_incompatible:" + volume.Role);
            if (string.IsNullOrWhiteSpace(volume.FileName) ||
                !string.Equals(Path.GetFileName(volume.FileName), volume.FileName, StringComparison.Ordinal))
                gaps.Add("terminal_volume_file_name_invalid:" + volume.Role);
            if (!IsHash(volume.Sha256)) gaps.Add("terminal_volume_hash_invalid:" + volume.Role);
            if (volume.Length < 0) gaps.Add("terminal_volume_length_invalid:" + volume.Role);
        }
        if (receipt.ExitCode == 0)
        {
            if (!IsProductRef(receipt.ProductRef)) gaps.Add("terminal_product_ref_invalid");
            if (!IsFullSha(receipt.SdkRef)) gaps.Add("terminal_sdk_ref_invalid");
            if (string.IsNullOrWhiteSpace(receipt.SnapshotFence)) gaps.Add("terminal_snapshot_fence_missing");
            var roles = volumes.Select(volume => volume.Role).ToArray();
            foreach (var role in AspxTerminalVolumeRoles.Required)
                if (roles.Count(value => string.Equals(value, role, StringComparison.Ordinal)) != 1)
                    gaps.Add("terminal_official_volume_binding_invalid:" + role);
        }
        return new(gaps.Count == 0,
            gaps.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    internal static async Task<AspxTerminalRunReceiptV1> ReadAndValidateAsync(string path,
        CancellationToken cancellationToken = default)
        => await ReadAndValidateAsync(path, null, cancellationToken);

    internal static async Task<AspxTerminalRunReceiptV1> ReadAndValidateAsync(string path,
        IReadOnlyList<AspxTerminalOutputSpec> officialOutputs,
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var receipt = JsonSerializer.Deserialize<AspxTerminalRunReceiptV1>(json,
            AspxInventoryRuntime.JsonOptions()) ?? throw new InvalidOperationException(
            "Terminal receipt JSON did not contain aspx-acquisition-terminal-receipt/v1.");
        var validation = Validate(receipt);
        if (!validation.Valid) throw new InvalidOperationException(
            "Terminal receipt validation failed: " + string.Join(", ", validation.GapCodes));
        var volumeGaps = await ValidateFreshVolumesAsync(path, receipt, officialOutputs, cancellationToken);
        if (volumeGaps.Count > 0) throw new InvalidOperationException(
            "Terminal receipt fresh-volume validation failed: " + string.Join(", ", volumeGaps));
        return receipt;
    }

    private static async Task<IReadOnlyList<string>> ValidateFreshVolumesAsync(string receiptPath,
        AspxTerminalRunReceiptV1 receipt, IReadOnlyList<AspxTerminalOutputSpec> officialOutputs,
        CancellationToken cancellationToken)
    {
        var gaps = new HashSet<string>(StringComparer.Ordinal);
        var supplied = (officialOutputs ?? Array.Empty<AspxTerminalOutputSpec>())
            .GroupBy(output => output.Role, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var pair in supplied.Where(pair => pair.Value.Length != 1))
            gaps.Add("terminal_official_path_binding_invalid:" + pair.Key);
        var receiptDirectory = Path.GetDirectoryName(Path.GetFullPath(receiptPath))!;
        foreach (var volume in receipt.Volumes ?? Array.Empty<AspxTerminalVolumeBinding>())
        {
            string volumePath;
            if (officialOutputs != null)
            {
                if (!supplied.TryGetValue(volume.Role, out var candidates) || candidates.Length != 1)
                {
                    gaps.Add("terminal_official_path_binding_missing:" + volume.Role);
                    continue;
                }
                var output = candidates[0];
                if (!string.Equals(output.OutputVersion, volume.OutputVersion, StringComparison.Ordinal))
                    gaps.Add("terminal_official_path_version_mismatch:" + volume.Role);
                volumePath = output.Path;
            }
            else
            {
                volumePath = Path.Combine(receiptDirectory, volume.FileName);
            }
            if (!string.Equals(Path.GetFileName(volumePath), volume.FileName, StringComparison.Ordinal))
                gaps.Add("terminal_official_file_name_mismatch:" + volume.Role);
            if (!File.Exists(volumePath))
            {
                gaps.Add("terminal_official_volume_missing:" + volume.Role);
                continue;
            }
            try
            {
                var actual = await AspxAggregateEvaluator.HashFileAsync(volumePath, cancellationToken);
                if (!string.Equals(actual.Hash, volume.Sha256, StringComparison.Ordinal))
                    gaps.Add("terminal_official_volume_hash_mismatch:" + volume.Role);
                if (actual.Length != volume.Length)
                    gaps.Add("terminal_official_volume_length_mismatch:" + volume.Role);
                await ValidateVolumeContentAsync(volume.Role, volumePath, receipt, gaps, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                gaps.Add("terminal_official_volume_read_failed:" + volume.Role + ":" + ex.GetType().Name);
            }
        }
        return gaps.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static async Task ValidateVolumeContentAsync(string role, string path,
        AspxTerminalRunReceiptV1 receipt, ISet<string> gaps, CancellationToken cancellationToken)
    {
        switch (role)
        {
            case AspxTerminalVolumeRoles.PhysicalDatabase:
                await ValidatePhysicalDatabaseAsync(path, receipt, gaps, cancellationToken);
                break;
            case AspxTerminalVolumeRoles.ReferenceDatabase:
                await ValidateReferenceDatabaseAsync(path, receipt, gaps, cancellationToken);
                break;
            case AspxTerminalVolumeRoles.PhysicalOutput:
            case AspxTerminalVolumeRoles.ReferenceOutput:
            case AspxTerminalVolumeRoles.AggregateOutput:
                await ValidateJsonOutputAsync(role, path, receipt, gaps, cancellationToken);
                break;
        }
    }

    private static async Task ValidatePhysicalDatabaseAsync(string path, AspxTerminalRunReceiptV1 receipt,
        ISet<string> gaps, CancellationToken cancellationToken)
    {
        var json = await ReadSqliteScalarAsync(path,
            "SELECT ManifestJson FROM DiscoveryRuns WHERE RunId=$runId", receipt.ArtifactRunId,
            cancellationToken);
        var manifest = JsonSerializer.Deserialize<DiscoveryRunManifest>(json,
            AspxInventoryRuntime.JsonOptions());
        ValidateManifestBinding(AspxTerminalVolumeRoles.PhysicalDatabase, manifest?.SchemaVersion,
            manifest?.ProductRef, manifest?.SdkRef, null, receipt, gaps);
    }

    private static async Task ValidateReferenceDatabaseAsync(string path, AspxTerminalRunReceiptV1 receipt,
        ISet<string> gaps, CancellationToken cancellationToken)
    {
        var json = await ReadSqliteScalarAsync(path,
            "SELECT ManifestJson FROM ReferenceRuns WHERE RunId=$runId", receipt.ArtifactRunId,
            cancellationToken);
        var manifest = JsonSerializer.Deserialize<AspxReferenceRunManifest>(json,
            AspxInventoryRuntime.JsonOptions());
        ValidateManifestBinding(AspxTerminalVolumeRoles.ReferenceDatabase, manifest?.SchemaVersion,
            manifest?.ProductRef, manifest?.SdkRef, manifest?.SnapshotFence, receipt, gaps);
    }

    private static void ValidateManifestBinding(string role, string outputVersion, string productRef,
        string sdkRef, string snapshotFence, AspxTerminalRunReceiptV1 receipt, ISet<string> gaps)
    {
        if (!string.Equals(outputVersion, AspxTerminalVolumeRoles.ExpectedVersions[role], StringComparison.Ordinal))
            gaps.Add("terminal_official_content_version_mismatch:" + role);
        if (!string.Equals(productRef, receipt.ProductRef, StringComparison.Ordinal))
            gaps.Add("terminal_official_product_ref_mismatch:" + role);
        if (!string.Equals(sdkRef, receipt.SdkRef, StringComparison.Ordinal))
            gaps.Add("terminal_official_sdk_ref_mismatch:" + role);
        if (snapshotFence != null && !string.Equals(snapshotFence, receipt.SnapshotFence, StringComparison.Ordinal))
            gaps.Add("terminal_official_snapshot_fence_mismatch:" + role);
    }

    private static async Task ValidateJsonOutputAsync(string role, string path,
        AspxTerminalRunReceiptV1 receipt, ISet<string> gaps, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var header = await JsonSerializer.DeserializeAsync<AspxTerminalJsonVolumeHeader>(stream,
            AspxInventoryRuntime.JsonOptions(), cancellationToken);
        if (!string.Equals(header?.OutputVersion, AspxTerminalVolumeRoles.ExpectedVersions[role],
                StringComparison.Ordinal))
            gaps.Add("terminal_official_content_version_mismatch:" + role);
        var runId = header?.RunId ?? header?.AcquisitionRunId;
        if (runId != receipt.ArtifactRunId) gaps.Add("terminal_official_run_id_mismatch:" + role);
        if (role != AspxTerminalVolumeRoles.AggregateOutput) return;
        if (!string.Equals(header.ProductRef, receipt.ProductRef, StringComparison.Ordinal))
            gaps.Add("terminal_official_product_ref_mismatch:" + role);
        if (!string.Equals(header.SdkRef, receipt.SdkRef, StringComparison.Ordinal))
            gaps.Add("terminal_official_sdk_ref_mismatch:" + role);
        foreach (var fence in new[] { header.PhysicalVolume?.SnapshotFence, header.ReferenceVolume?.SnapshotFence })
            if (!string.Equals(fence, receipt.SnapshotFence, StringComparison.Ordinal))
                gaps.Add("terminal_official_snapshot_fence_mismatch:" + role);
    }

    private static async Task<string> ReadSqliteScalarAsync(string path, string commandText, Guid runId,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        return (string)await command.ExecuteScalarAsync(cancellationToken);
    }

    private sealed record AspxTerminalJsonVolumeHeader(
        string OutputVersion,
        Guid? RunId,
        Guid? AcquisitionRunId,
        string ProductRef,
        string SdkRef,
        AspxTerminalEnvelopeVolumeHeader PhysicalVolume,
        AspxTerminalEnvelopeVolumeHeader ReferenceVolume);

    private sealed record AspxTerminalEnvelopeVolumeHeader(string SnapshotFence);

    private static void ValidateExecutable(AspxManagedExecutableBinding executable, ISet<string> gaps)
    {
        if (executable == null)
        {
            gaps.Add("terminal_executable_binding_missing");
            return;
        }
        if (string.IsNullOrWhiteSpace(executable.AssemblyName)) gaps.Add("terminal_assembly_name_missing");
        if (string.IsNullOrWhiteSpace(executable.PackageVersion)) gaps.Add("terminal_package_version_missing");
        if (!IsHash(executable.ExecutableSha256) || executable.ExecutableLength == null)
            gaps.Add("terminal_executable_file_binding_invalid");
        if (!IsHash(executable.DependencyGraphSha256)) gaps.Add("terminal_dependency_graph_hash_invalid");
        if (executable.DependencyGraphEntryCount < 0) gaps.Add("terminal_dependency_graph_count_invalid");
        if (executable.DependencyManifestFileName != null &&
            (!IsHash(executable.DependencyManifestSha256) || executable.DependencyManifestLength == null))
            gaps.Add("terminal_dependency_manifest_binding_invalid");
    }

    private static bool IsProductRef(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.LastIndexOf('@');
        return separator > 0 && IsFullSha(value[(separator + 1)..]);
    }

    private static bool IsFullSha(string value) => value?.Length == 40 && value.All(Uri.IsHexDigit);
    private static bool IsHash(string value) => value?.Length == 64 && value.All(Uri.IsHexDigit) &&
        string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal);
}

internal static class AspxTerminalRunReceiptWriter
{
    internal static async Task<AspxTerminalRunReceiptV1> WriteAsync(string path, Guid artifactRunId,
        int exitCode, string completionState, string productRef, string sdkRef, string snapshotFence,
        string aggregateVerdict, AspxManagedExecutableBinding executable,
        IReadOnlyList<AspxTerminalOutputSpec> outputs, string errorCode = null, string errorDigest = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var volumes = new List<AspxTerminalVolumeBinding>();
        foreach (var output in outputs ?? Array.Empty<AspxTerminalOutputSpec>())
        {
            if (!File.Exists(output.Path))
            {
                if (exitCode == 0) throw new InvalidOperationException(
                    $"Cannot seal terminal success: official volume '{output.Role}' is missing.");
                continue;
            }
            var value = await AspxAggregateEvaluator.HashFileAsync(output.Path, cancellationToken);
            volumes.Add(new(output.Role, output.OutputVersion, Path.GetFileName(output.Path),
                value.Hash, value.Length));
        }
        var receipt = new AspxTerminalRunReceiptV1(AspxTerminalRunReceiptV1.Version, artifactRunId,
            exitCode, completionState, productRef, sdkRef, snapshotFence, aggregateVerdict, executable,
            volumes.OrderBy(volume => volume.Role, StringComparer.Ordinal).ToArray(),
            DateTimeOffset.UtcNow, errorCode, errorDigest);
        var validation = AspxTerminalRunReceiptValidator.Validate(receipt);
        if (!validation.Valid) throw new InvalidOperationException(
            "Cannot seal terminal receipt: " + string.Join(", ", validation.GapCodes));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(receipt, AspxInventoryRuntime.JsonOptions(indented: true));
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            var verified = await AspxTerminalRunReceiptValidator.ReadAndValidateAsync(temporaryPath, outputs,
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
            return verified;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
