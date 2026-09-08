using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Services;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Security.Cryptography;
using System.Text.Json;

namespace PnP.Scanning.Process.Commands;

internal sealed record AspxInventoryCliOptions(
    FileInfo Input,
    FileInfo Manifest,
    FileInfo Database,
    FileInfo Output,
    Guid? ResumeRunId)
{
    internal Mode Mode => Mode.AspxInventory;
}

internal static class AspxInventoryCommandDefinition
{
    internal static Command Create(Func<AspxInventoryCliOptions, CancellationToken, Task<int>> executeAsync)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
        var command = new Command("aspx-inventory",
            "Runs versioned ASPX discovery from a sealed hierarchy/source input.");
        var input = ExistingRequiredFile("--input", "ASPX discovery input JSON.");
        var manifest = ExistingRequiredFile("--manifest", "Immutable DiscoveryRunManifest JSON.");
        var database = new Option<FileInfo>("--database", "SQLite discovery ledger path.") { IsRequired = true };
        var output = new Option<FileInfo>("--output", "Versioned ASPX Discovery Output JSON path.") { IsRequired = true };
        var resume = new Option<Guid?>("--resume-run-id", "Persisted run id to resume after provenance validation.");
        foreach (var option in new Option[] { input, manifest, database, output, resume }) command.AddOption(option);

        command.AddValidator(result =>
        {
            if (result.GetValueForOption(resume) == null && result.GetValueForOption(database)?.Exists == true)
                result.ErrorMessage = "An existing --database requires --resume-run-id; new runs fail closed instead of overwriting a ledger.";
            var inputPath = result.GetValueForOption(input)?.FullName;
            var databasePath = result.GetValueForOption(database)?.FullName;
            var outputPath = result.GetValueForOption(output)?.FullName;
            if (string.Equals(inputPath, databasePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(databasePath, outputPath, StringComparison.OrdinalIgnoreCase))
                result.ErrorMessage = "--input, --database and --output must be distinct paths.";
        });
        command.SetHandler(async (InvocationContext context) =>
        {
            var parse = context.ParseResult;
            var options = new AspxInventoryCliOptions(
                parse.GetValueForOption(input), parse.GetValueForOption(manifest),
                parse.GetValueForOption(database), parse.GetValueForOption(output),
                parse.GetValueForOption(resume));
            context.ExitCode = await executeAsync(options, context.GetCancellationToken());
        });
        return command;
    }

    private static Option<FileInfo> ExistingRequiredFile(string name, string description)
    {
        var option = new Option<FileInfo>(name, description) { IsRequired = true };
        option.ExistingOnly();
        return option;
    }
}

internal sealed class AspxInventoryCommandHandler
{
    internal Command Create() => AspxInventoryCommandDefinition.Create(ExecuteAsync);

    private static async Task<int> ExecuteAsync(AspxInventoryCliOptions options, CancellationToken cancellationToken)
    {
        try
        {
            if (options.Mode != Mode.AspxInventory)
                throw new InvalidOperationException("Unexpected runtime mode binding.");
            var manifestJson = await File.ReadAllTextAsync(options.Manifest.FullName, cancellationToken);
            var manifest = JsonSerializer.Deserialize<DiscoveryRunManifest>(manifestJson,
                AspxInventoryRuntime.JsonOptions())
                ?? throw new InvalidOperationException("Manifest JSON did not contain a DiscoveryRunManifest.");
            if (manifest.ContractVersion != DiscoveryRunManifest.CurrentContractVersion ||
                manifest.SchemaVersion != DiscoveryRunManifest.CurrentSchemaVersion)
                throw new InvalidOperationException("Manifest contract/schema version is unsupported by this runtime.");

            var inputBytes = await File.ReadAllBytesAsync(options.Input.FullName, cancellationToken);
            var inputHash = Convert.ToHexString(SHA256.HashData(inputBytes)).ToLowerInvariant();
            if (!string.Equals(manifest.InputManifestHash, inputHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Input manifest SHA-256 does not match DiscoveryRunManifest.InputManifestHash.");

            using var provider = await ManifestAspxDiscoveryProvider.LoadAsync(options.Input.FullName, cancellationToken);
            var input = provider.Input;
            if (input.FixtureRun && !string.Equals(manifest.FixtureHash, inputHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture SHA-256 does not match DiscoveryRunManifest.FixtureHash.");
            var output = await new AspxInventoryRuntime().RunAsync(provider,
                new(options.Database.FullName, options.Output.FullName, manifest, input.ScopeMode,
                    input.FixtureRun, options.ResumeRunId, input.TenantVisibilityVerified), cancellationToken);
            AnsiConsole.MarkupLine($"[green]ASPX inventory {output.RunId:D} finished with {output.CoverageVerdict}; output: {Markup.Escape(options.Output.FullName)}[/]");
            return 0;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]ASPX inventory cancelled.[/]");
            return 2;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]ASPX inventory failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
