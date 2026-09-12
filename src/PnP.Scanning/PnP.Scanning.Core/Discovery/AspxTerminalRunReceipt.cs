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
        foreach (var volume in receipt.Volumes ?? Array.Empty<AspxTerminalVolumeBinding>())
        {
            if (!AspxTerminalVolumeRoles.Required.Contains(volume.Role, StringComparer.Ordinal))
                gaps.Add("terminal_volume_role_unknown:" + volume.Role);
            if (string.IsNullOrWhiteSpace(volume.OutputVersion)) gaps.Add("terminal_volume_version_missing:" + volume.Role);
            if (!IsHash(volume.Sha256)) gaps.Add("terminal_volume_hash_invalid:" + volume.Role);
            if (volume.Length < 0) gaps.Add("terminal_volume_length_invalid:" + volume.Role);
        }
        if (receipt.ExitCode == 0)
        {
            if (!IsProductRef(receipt.ProductRef)) gaps.Add("terminal_product_ref_invalid");
            if (!IsFullSha(receipt.SdkRef)) gaps.Add("terminal_sdk_ref_invalid");
            if (string.IsNullOrWhiteSpace(receipt.SnapshotFence)) gaps.Add("terminal_snapshot_fence_missing");
            var roles = (receipt.Volumes ?? Array.Empty<AspxTerminalVolumeBinding>())
                .Select(volume => volume.Role).ToArray();
            foreach (var role in AspxTerminalVolumeRoles.Required)
                if (roles.Count(value => string.Equals(value, role, StringComparison.Ordinal)) != 1)
                    gaps.Add("terminal_official_volume_binding_invalid:" + role);
        }
        return new(gaps.Count == 0,
            gaps.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    internal static async Task<AspxTerminalRunReceiptV1> ReadAndValidateAsync(string path,
        CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var receipt = JsonSerializer.Deserialize<AspxTerminalRunReceiptV1>(json,
            AspxInventoryRuntime.JsonOptions()) ?? throw new InvalidOperationException(
            "Terminal receipt JSON did not contain aspx-acquisition-terminal-receipt/v1.");
        var validation = Validate(receipt);
        if (!validation.Valid) throw new InvalidOperationException(
            "Terminal receipt validation failed: " + string.Join(", ", validation.GapCodes));
        return receipt;
    }

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
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
        return receipt;
    }
}
