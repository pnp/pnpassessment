using Microsoft.AspNetCore.DataProtection;
using PnP.Core.Auth;
using PnP.Core.Services;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Discovery;
using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;

namespace PnP.Scanning.Process.Commands;

internal sealed record AspxAcquisitionCliOptions(
    IReadOnlyList<string> Sites,
    string Tenant,
    AuthenticationMode AuthMode,
    Guid ApplicationId,
    string TenantId,
    string CertPath,
    FileInfo CertFile,
    string CertPassword,
    FileInfo Manifest,
    FileInfo Registry,
    FileInfo PhysicalDatabase,
    FileInfo PhysicalOutput,
    FileInfo ReferenceDatabase,
    FileInfo ReferenceOutput,
    FileInfo AggregateOutput,
    string PlatformBuild,
    string SnapshotFence,
    string PermissionContext,
    string VisibilityBoundary,
    string AuthorityRevision,
    string AuthorityHash,
    Guid? ResumeRunId);

internal static class AspxAcquisitionCommandDefinition
{
    internal static Command Create(Func<AspxAcquisitionCliOptions, CancellationToken, Task<int>> executeAsync)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
        var command = new Command("aspx-acquisition",
            "Runs authenticated SharePoint live ASPX acquisition and writes physical v2, reference v1, and aggregate v1 volumes. Use aspx-inventory for explicit offline manifest replay.");
        var sites = new Option<List<string>>("--site", "Authorized site collection URL. Repeat for multiple site collections.")
        {
            IsRequired = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var tenant = RequiredString("--tenant", "SharePoint tenant host or URL used by the existing Assessment authentication path.");
        var authMode = new Option<AuthenticationMode>("--authMode", () => AuthenticationMode.Interactive,
            "Existing Assessment authentication mode: Interactive, Device, or Application.");
        var applicationId = new Option<Guid>("--applicationId", "Entra application id used by the existing Assessment authentication path.") { IsRequired = true };
        var tenantId = new Option<string>("--tenantId", "Optional Entra tenant id.");
        var certPath = new Option<string>("--certPath", "Application-mode certificate store path.");
        var certFile = new Option<FileInfo>("--certFile", "Application-mode PFX file.");
        certFile.ExistingOnly();
        var certPassword = new Option<string>("--certPassword", "Application-mode PFX password; never persisted in acquisition outputs.");
        var manifest = RequiredFile("--manifest", "Immutable physical DiscoveryRunManifest JSON.");
        var registry = RequiredFile("--registry", "Independent aspx-platform-registry/v1 JSON.");
        var physicalDatabase = RequiredOutput("--physical-database", "aspx-discovery-sqlite/v2 path.");
        var physicalOutput = RequiredOutput("--physical-output", "aspx-discovery-output/v2 path.");
        var referenceDatabase = RequiredOutput("--reference-database", "aspx-reference-sqlite/v1 path.");
        var referenceOutput = RequiredOutput("--reference-output", "aspx-reference-output/v1 path.");
        var aggregateOutput = RequiredOutput("--aggregate-output", "aspx-acquisition-verdict/v1 path.");
        var platformBuild = RequiredString("--platform-build", "Observed SharePoint platform build bound to the registry.");
        var snapshotFence = RequiredString("--snapshot-fence", "Immutable acquisition snapshot/as-of fence.");
        var permissionContext = RequiredString("--permission-context", "Non-secret effective identity/permission context label.");
        var visibilityBoundary = RequiredString("--visibility-boundary", "Authorized visibility boundary proven by this run.");
        var authorityRevision = RequiredString("--authority-revision", "Independent scope authority revision.");
        var authorityHash = RequiredString("--authority-hash", "SHA-256 of the independent scope authority artifact.");
        var resume = new Option<Guid?>("--resume-run-id", "Exact acquisition run id to resume after both manifests validate.");
        foreach (var option in new Option[]
        {
            sites, tenant, authMode, applicationId, tenantId, certPath, certFile, certPassword,
            manifest, registry, physicalDatabase, physicalOutput, referenceDatabase, referenceOutput,
            aggregateOutput, platformBuild, snapshotFence, permissionContext, visibilityBoundary,
            authorityRevision, authorityHash, resume,
        }) command.AddOption(option);

        command.AddValidator(result =>
        {
            var outputs = new[]
            {
                result.GetValueForOption(physicalDatabase)?.FullName,
                result.GetValueForOption(physicalOutput)?.FullName,
                result.GetValueForOption(referenceDatabase)?.FullName,
                result.GetValueForOption(referenceOutput)?.FullName,
                result.GetValueForOption(aggregateOutput)?.FullName,
            }.Where(value => value != null).ToArray();
            if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length)
                result.ErrorMessage = "All physical, reference, and aggregate output paths must be distinct.";
            if (result.GetValueForOption(resume) == null && outputs.Any(File.Exists))
                result.ErrorMessage = "Existing acquisition outputs require --resume-run-id; new runs never overwrite ledgers implicitly.";
            var parsedSites = result.GetValueForOption(sites) ?? new List<string>();
            if (parsedSites.Any(site => !Uri.TryCreate(site, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
                result.ErrorMessage = "Every --site value must be an absolute HTTPS URL.";
            var hash = result.GetValueForOption(authorityHash);
            if (hash?.Length != 64 || !hash.All(Uri.IsHexDigit))
                result.ErrorMessage = "--authority-hash must be a 64-character SHA-256 hex value.";
        });
        command.SetHandler(async (InvocationContext context) =>
        {
            var parse = context.ParseResult;
            var options = new AspxAcquisitionCliOptions(
                parse.GetValueForOption(sites), parse.GetValueForOption(tenant),
                parse.GetValueForOption(authMode), parse.GetValueForOption(applicationId),
                parse.GetValueForOption(tenantId), parse.GetValueForOption(certPath),
                parse.GetValueForOption(certFile), parse.GetValueForOption(certPassword),
                parse.GetValueForOption(manifest), parse.GetValueForOption(registry),
                parse.GetValueForOption(physicalDatabase), parse.GetValueForOption(physicalOutput),
                parse.GetValueForOption(referenceDatabase), parse.GetValueForOption(referenceOutput),
                parse.GetValueForOption(aggregateOutput), parse.GetValueForOption(platformBuild),
                parse.GetValueForOption(snapshotFence), parse.GetValueForOption(permissionContext),
                parse.GetValueForOption(visibilityBoundary), parse.GetValueForOption(authorityRevision),
                parse.GetValueForOption(authorityHash).ToLowerInvariant(), parse.GetValueForOption(resume));
            context.ExitCode = await executeAsync(options, context.GetCancellationToken());
        });
        return command;
    }

    private static Option<FileInfo> RequiredFile(string name, string description)
    {
        var option = new Option<FileInfo>(name, description) { IsRequired = true };
        option.ExistingOnly();
        return option;
    }

    private static Option<FileInfo> RequiredOutput(string name, string description) =>
        new(name, description) { IsRequired = true };

    private static Option<string> RequiredString(string name, string description) =>
        new(name, description) { IsRequired = true };
}

internal sealed class AspxAcquisitionCommandHandler
{
    private readonly IPnPContextFactory contextFactory;
    private readonly IDataProtectionProvider dataProtectionProvider;
    private readonly ConfigurationOptions configuration;

    internal AspxAcquisitionCommandHandler(IPnPContextFactory contextFactory,
        IDataProtectionProvider dataProtectionProvider, ConfigurationOptions configuration)
    {
        this.contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        this.dataProtectionProvider = dataProtectionProvider ?? throw new ArgumentNullException(nameof(dataProtectionProvider));
        this.configuration = configuration;
    }

    internal Command Create() => AspxAcquisitionCommandDefinition.Create(ExecuteAsync);

    private async Task<int> ExecuteAsync(AspxAcquisitionCliOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<DiscoveryRunManifest>(
                await File.ReadAllTextAsync(options.Manifest.FullName, cancellationToken),
                AspxInventoryRuntime.JsonOptions())
                ?? throw new InvalidOperationException("Manifest JSON did not contain a DiscoveryRunManifest.");
            if (manifest.ContractVersion != DiscoveryRunManifest.CurrentContractVersion ||
                manifest.SchemaVersion != DiscoveryRunManifest.CurrentSchemaVersion)
                throw new InvalidOperationException("Physical manifest must remain aspx-discovery/v2 + aspx-discovery-sqlite/v2.");
            var registry = JsonSerializer.Deserialize<AspxPlatformRegistryV1>(
                await File.ReadAllTextAsync(options.Registry.FullName, cancellationToken),
                AspxInventoryRuntime.JsonOptions())
                ?? throw new InvalidOperationException("Registry JSON did not contain aspx-platform-registry/v1.");
            var registryInvalid = registry.Validate(options.PlatformBuild);
            if (registryInvalid.Count > 0)
                throw new InvalidOperationException("Independent registry cannot close this build: " + string.Join(", ", registryInvalid));

            var environment = Microsoft365Environment.Production;
            if (!string.IsNullOrWhiteSpace(configuration?.Environment) &&
                Enum.TryParse(configuration.Environment, out Microsoft365Environment configured))
                environment = configured;
            var authentication = new AuthenticationManager(dataProtectionProvider);
            await authentication.VerifyAuthenticationAsync(options.Tenant, options.AuthMode.ToString(), environment,
                options.ApplicationId, options.TenantId, options.CertPath, options.CertFile, options.CertPassword,
                deviceCode =>
                {
                    AnsiConsole.MarkupLine(Markup.Escape(deviceCode.Message));
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            var authProvider = new ExternalAuthenticationProvider((_, scopes) =>
                authentication.GetAccessTokenAsync(scopes));
            using var clientFactory = new PnPContextSharePointAspxRestClientFactory(contextFactory, authProvider);
            using var provider = new SharePointLiveAspxDiscoveryProvider(
                new(options.Sites.Select(site => new Uri(site)).ToArray(), options.PermissionContext,
                    options.VisibilityBoundary, options.AuthorityRevision, options.AuthorityHash), clientFactory);
            var permissionHash = DiscoveryHash.Of(options.PermissionContext, options.VisibilityBoundary,
                options.AuthMode.ToString(), options.TenantId ?? string.Empty);
            var result = await new AspxAcquisitionRuntime().RunAsync(provider,
                new(options.PhysicalDatabase.FullName, options.PhysicalOutput.FullName,
                    options.ReferenceDatabase.FullName, options.ReferenceOutput.FullName,
                    options.AggregateOutput.FullName, manifest, "declared_subset", FixtureRun: false,
                    TenantVisibilityVerified: false, permissionHash, options.PlatformBuild,
                    options.SnapshotFence, registry, options.ResumeRunId), cancellationToken).ConfigureAwait(false);
            AnsiConsole.MarkupLine($"[green]ASPX live acquisition {result.Aggregate.AcquisitionRunId:D} finished with {result.Aggregate.AggregateVerdict}; physical={Markup.Escape(options.PhysicalOutput.FullName)}; reference={Markup.Escape(options.ReferenceOutput.FullName)}; aggregate={Markup.Escape(options.AggregateOutput.FullName)}[/]");
            return 0;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]ASPX live acquisition cancelled.[/]");
            return 2;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]ASPX live acquisition failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
