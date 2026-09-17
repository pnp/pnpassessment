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
    string ScopeMode,
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
    FileInfo TerminalReceipt,
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
            "Runs authenticated SharePoint live ASPX acquisition and writes physical v2, reference v2, aggregate v2, and product terminal receipt v1 volumes. Use aspx-inventory for explicit offline manifest replay.");
        var sites = new Option<List<string>>("--site", "Authorized site collection URL. Repeat for declared_subset mode.")
        {
            AllowMultipleArgumentsPerToken = true,
        };
        var scopeMode = new Option<string>("--scope-mode", () => AspxScopeModes.DeclaredSubset,
            $"Scope authority mode: {AspxScopeModes.ProductTenantAuthority} or {AspxScopeModes.DeclaredSubset}.");
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
        var physicalDatabase = RequiredOutput("--physical-database", "classic-page-discovery-sqlite/v3 path.");
        var physicalOutput = RequiredOutput("--physical-output", "classic-page-discovery-output/v3 path.");
        var referenceDatabase = RequiredOutput("--reference-database", "aspx-reference-sqlite/v2 path.");
        var referenceOutput = RequiredOutput("--reference-output", "aspx-reference-output/v2 path.");
        var aggregateOutput = RequiredOutput("--aggregate-output", "aspx-acquisition-verdict/v2 path.");
        var terminalReceipt = RequiredOutput("--terminal-receipt", "aspx-acquisition-terminal-receipt/v1 path.");
        var platformBuild = RequiredString("--platform-build", "Observed SharePoint platform build bound to the registry.");
        var snapshotFence = RequiredString("--snapshot-fence", "Immutable acquisition snapshot/as-of fence.");
        var permissionContext = RequiredString("--permission-context", "Non-secret effective identity/permission context label.");
        var visibilityBoundary = RequiredString("--visibility-boundary", "Authorized visibility boundary proven by this run.");
        var authorityRevision = new Option<string>("--authority-revision",
            "Deprecated declared-subset authority label retained only for CLI compatibility; product authority is computed by this command.");
        var authorityHash = new Option<string>("--authority-hash",
            "Deprecated declared-subset authority hash retained only for CLI compatibility; product authority is computed by this command.");
        var resume = new Option<Guid?>("--resume-run-id", "Exact acquisition run id to resume after both manifests validate.");
        foreach (var option in new Option[]
        {
            sites, scopeMode, tenant, authMode, applicationId, tenantId, certPath, certFile, certPassword,
            manifest, registry, physicalDatabase, physicalOutput, referenceDatabase, referenceOutput,
            aggregateOutput, terminalReceipt, platformBuild, snapshotFence, permissionContext, visibilityBoundary,
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
                result.GetValueForOption(terminalReceipt)?.FullName,
            }.Where(value => value != null).ToArray();
            if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length)
                result.ErrorMessage = "All physical, reference, and aggregate output paths must be distinct.";
            if (result.GetValueForOption(resume) == null && outputs.Any(File.Exists))
                result.ErrorMessage = "Existing acquisition outputs require --resume-run-id; new runs never overwrite ledgers implicitly.";
            var parsedSites = result.GetValueForOption(sites) ?? new List<string>();
            if (parsedSites.Any(site => !Uri.TryCreate(site, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
                result.ErrorMessage = "Every --site value must be an absolute HTTPS URL.";
            var parsedMode = result.GetValueForOption(scopeMode);
            if (parsedMode is not (AspxScopeModes.ProductTenantAuthority or AspxScopeModes.DeclaredSubset))
                result.ErrorMessage = $"--scope-mode must be '{AspxScopeModes.ProductTenantAuthority}' or '{AspxScopeModes.DeclaredSubset}'.";
            if (parsedMode == AspxScopeModes.DeclaredSubset && parsedSites.Count == 0)
                result.ErrorMessage = "declared_subset requires at least one --site value.";
            if (parsedMode == AspxScopeModes.ProductTenantAuthority && parsedSites.Count > 0)
                result.ErrorMessage = "product_tenant_authority enumerates its own independent site denominator; do not pass --site.";
            var hash = result.GetValueForOption(authorityHash);
            if (!string.IsNullOrWhiteSpace(hash) && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
                result.ErrorMessage = "When supplied, --authority-hash must be a 64-character SHA-256 hex value.";
        });
        command.SetHandler(async (InvocationContext context) =>
        {
            var parse = context.ParseResult;
            var options = new AspxAcquisitionCliOptions(
                parse.GetValueForOption(sites), parse.GetValueForOption(scopeMode), parse.GetValueForOption(tenant),
                parse.GetValueForOption(authMode), parse.GetValueForOption(applicationId),
                parse.GetValueForOption(tenantId), parse.GetValueForOption(certPath),
                parse.GetValueForOption(certFile), parse.GetValueForOption(certPassword),
                parse.GetValueForOption(manifest), parse.GetValueForOption(registry),
                parse.GetValueForOption(physicalDatabase), parse.GetValueForOption(physicalOutput),
                parse.GetValueForOption(referenceDatabase), parse.GetValueForOption(referenceOutput),
                parse.GetValueForOption(aggregateOutput), parse.GetValueForOption(terminalReceipt),
                parse.GetValueForOption(platformBuild),
                parse.GetValueForOption(snapshotFence), parse.GetValueForOption(permissionContext),
                parse.GetValueForOption(visibilityBoundary), parse.GetValueForOption(authorityRevision),
                parse.GetValueForOption(authorityHash)?.ToLowerInvariant(), parse.GetValueForOption(resume));
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
        var artifactRunId = options.ResumeRunId ?? Guid.NewGuid();
        DiscoveryRunManifest manifest = null;
        AspxAcquisitionVerdictV2 aggregate = null;
        var exitCode = 1;
        var completionState = "Failed";
        string errorCode = null;
        string errorDigest = null;
        var terminalPathSafe = true;
        try
        {
            manifest = JsonSerializer.Deserialize<DiscoveryRunManifest>(
                await File.ReadAllTextAsync(options.Manifest.FullName, cancellationToken),
                AspxInventoryRuntime.JsonOptions())
                ?? throw new InvalidOperationException("Manifest JSON did not contain a DiscoveryRunManifest.");
            if (manifest.ContractVersion != DiscoveryRunManifest.CurrentContractVersion ||
                manifest.SchemaVersion != DiscoveryRunManifest.CurrentSchemaVersion)
                throw new InvalidOperationException("Physical manifest must use the current classic page discovery contract and schema versions.");
            var registry = JsonSerializer.Deserialize<AspxPlatformRegistryV1>(
                await File.ReadAllTextAsync(options.Registry.FullName, cancellationToken),
                AspxInventoryRuntime.JsonOptions())
                ?? throw new InvalidOperationException("Registry JSON did not contain aspx-platform-registry/v1.");
            var registryInvalid = registry.Validate(options.PlatformBuild);
            if (registryInvalid.Count > 0)
                throw new InvalidOperationException("Independent registry cannot close this build: " + string.Join(", ", registryInvalid));
            if (options.ResumeRunId != null && File.Exists(options.TerminalReceipt.FullName))
            {
                AspxTerminalRunReceiptV1 previous;
                try
                {
                    previous = await AspxTerminalRunReceiptValidator.ReadAndValidateAsync(
                        options.TerminalReceipt.FullName, OutputSpecs(options), cancellationToken);
                }
                catch
                {
                    terminalPathSafe = false;
                    throw;
                }
                if (previous.ArtifactRunId != artifactRunId ||
                    !string.Equals(previous.SnapshotFence, options.SnapshotFence, StringComparison.Ordinal))
                {
                    terminalPathSafe = false;
                    throw new InvalidOperationException(
                        "Terminal resume rejected: the existing receipt is bound to a different run or snapshot fence. Preserve it and use a new terminal receipt path.");
                }
            }

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
            var tenantRoot = new Uri(AuthenticationManager.GetSiteFromTenant(options.Tenant));
            var authority = await AspxTenantAuthorityCapture.CaptureAsync(options.ScopeMode, tenantRoot,
                options.Sites.Select(site => new Uri(site)).ToArray(),
                new PnPCoreAspxTenantAuthorityAdapter(contextFactory, authProvider), cancellationToken)
                .ConfigureAwait(false);
            manifest = manifest with
            {
                ScopePolicyHash = authority.AuthorityHash,
                TenantManifestHash = authority.AuthorityHash,
            };
            using var clientFactory = new PnPContextSharePointAspxRestClientFactory(contextFactory, authProvider);
            using var provider = new SharePointLiveAspxDiscoveryProvider(
                new(authority.Sites.Items.Select(site => site.Url).ToArray(), options.PermissionContext,
                    options.VisibilityBoundary, authority.AuthorityRevision, authority.AuthorityHash,
                    options.PlatformBuild, authority), clientFactory);
            var permissionHash = DiscoveryHash.Of(options.PermissionContext, options.VisibilityBoundary,
                options.AuthMode.ToString(), options.TenantId ?? string.Empty, authority.ScopeMode,
                authority.AuthorityHash);
            var result = await new AspxAcquisitionRuntime().RunAsync(provider,
                new(options.PhysicalDatabase.FullName, options.PhysicalOutput.FullName,
                    options.ReferenceDatabase.FullName, options.ReferenceOutput.FullName,
                    options.AggregateOutput.FullName, manifest, authority.ScopeMode, FixtureRun: false,
                    TenantVisibilityVerified: authority.TenantVisibilityVerified, permissionHash, options.PlatformBuild,
                    options.SnapshotFence, registry, options.ResumeRunId,
                    options.ResumeRunId == null ? artifactRunId : null), cancellationToken).ConfigureAwait(false);
            aggregate = result.Aggregate;
            exitCode = 0;
            completionState = "Succeeded";
        }
        catch (OperationCanceledException ex)
        {
            AnsiConsole.MarkupLine("[yellow]ASPX live acquisition cancelled.[/]");
            exitCode = 2;
            completionState = "Cancelled";
            errorCode = "operation_cancelled";
            errorDigest = DiscoveryHash.Of(ex.GetType().FullName, ex.Message ?? string.Empty);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]ASPX live acquisition failed: {Markup.Escape(ex.Message)}[/]");
            exitCode = 1;
            completionState = "Failed";
            errorCode = "acquisition_failed";
            errorDigest = DiscoveryHash.Of(ex.GetType().FullName, ex.Message ?? string.Empty);
        }

        if (!terminalPathSafe) return exitCode;
        try
        {
            var executable = await AspxManagedExecutableBinding.CaptureAsync(CancellationToken.None);
            await AspxTerminalRunReceiptWriter.WriteAsync(options.TerminalReceipt.FullName, artifactRunId,
                exitCode, completionState, manifest?.ProductRef, manifest?.SdkRef, options.SnapshotFence,
                aggregate?.AggregateVerdict.ToString(), executable, OutputSpecs(options), errorCode,
                errorDigest, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]ASPX live acquisition terminal receipt failed: {Markup.Escape(ex.Message)}[/]");
            return 3;
        }
        if (exitCode == 0)
            AnsiConsole.MarkupLine($"[green]ASPX live acquisition {artifactRunId:D} finished with {aggregate.AggregateVerdict}; scopeMode={Markup.Escape(options.ScopeMode)}; physical={Markup.Escape(options.PhysicalOutput.FullName)}; reference={Markup.Escape(options.ReferenceOutput.FullName)}; aggregate={Markup.Escape(options.AggregateOutput.FullName)}; terminal={Markup.Escape(options.TerminalReceipt.FullName)}[/]");
        return exitCode;
    }

    private static IReadOnlyList<AspxTerminalOutputSpec> OutputSpecs(AspxAcquisitionCliOptions options) =>
        new[]
        {
            new AspxTerminalOutputSpec(AspxTerminalVolumeRoles.PhysicalDatabase,
                DiscoveryRunManifest.CurrentSchemaVersion, options.PhysicalDatabase.FullName),
            new AspxTerminalOutputSpec(AspxTerminalVolumeRoles.PhysicalOutput,
                AspxDiscoveryOutputV2.Version, options.PhysicalOutput.FullName),
            new AspxTerminalOutputSpec(AspxTerminalVolumeRoles.ReferenceDatabase,
                AspxAcquisitionVersions.ReferenceStore, options.ReferenceDatabase.FullName),
            new AspxTerminalOutputSpec(AspxTerminalVolumeRoles.ReferenceOutput,
                AspxReferenceOutputV2.Version, options.ReferenceOutput.FullName),
            new AspxTerminalOutputSpec(AspxTerminalVolumeRoles.AggregateOutput,
                AspxAcquisitionVerdictV2.Version, options.AggregateOutput.FullName),
        };
}
