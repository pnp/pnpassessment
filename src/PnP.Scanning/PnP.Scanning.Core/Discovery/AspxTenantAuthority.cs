using PnP.Core;
using PnP.Core.Admin.Model.SharePoint;
using PnP.Core.Auth;
using PnP.Core.Services;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

internal static class AspxScopeModes
{
    internal const string TenantFull = "tenant_full";
    internal const string DeclaredSubset = "declared_subset";
    internal const string ProductTenantAuthority = "product_tenant_authority";

    internal static bool IsKnown(string value) => value is TenantFull or DeclaredSubset or ProductTenantAuthority;
    internal static bool RequiresTenantDenominator(string value) => value is TenantFull or ProductTenantAuthority;
    internal static bool CanVerifyTenant(string value) => value == ProductTenantAuthority;
}

internal sealed record AspxAuthoritySite(
    Guid SiteId,
    Guid RootWebId,
    Uri Url,
    string GraphId,
    string Name);

internal sealed record AspxAuthorityWeb(
    Guid WebId,
    Uri Url,
    string ServerRelativeUrl,
    Uri ParentWebUrl,
    string WebTemplateConfiguration,
    bool IsRootWeb);

internal sealed record AspxAuthorityCollection<T>(
    DiscoveryTerminalOutcome Outcome,
    IReadOnlyList<T> Items,
    string Provider,
    string Operation,
    string ActualFilter,
    IReadOnlyList<string> Exclusions,
    string FailureCode,
    string FailureDetail,
    bool ContinuationRemaining,
    DateTimeOffset CompletedAtUtc)
{
    internal bool IsTerminalSuccess => Outcome is DiscoveryTerminalOutcome.Complete or DiscoveryTerminalOutcome.Empty;
}

internal sealed record AspxSiteWebAuthority(
    AspxAuthoritySite Site,
    AspxAuthorityCollection<AspxAuthorityWeb> Webs);

internal sealed record AspxTenantAuthoritySnapshot(
    string ContractVersion,
    string ScopeMode,
    Uri TenantRoot,
    string AuthorityRevision,
    string AuthorityHash,
    AspxAuthorityCollection<AspxAuthoritySite> Sites,
    IReadOnlyList<AspxSiteWebAuthority> SiteWebs,
    bool TenantVisibilityVerified)
{
    internal const string CurrentContractVersion = "aspx-tenant-authority/v1";

    internal static AspxTenantAuthoritySnapshot Freeze(string scopeMode, Uri tenantRoot,
        AspxAuthorityCollection<AspxAuthoritySite> sites, IReadOnlyList<AspxSiteWebAuthority> siteWebs)
    {
        if (!AspxScopeModes.IsKnown(scopeMode))
            throw new ArgumentException($"Unknown ASPX scope mode '{scopeMode}'.", nameof(scopeMode));
        ArgumentNullException.ThrowIfNull(tenantRoot);
        ArgumentNullException.ThrowIfNull(sites);
        siteWebs ??= Array.Empty<AspxSiteWebAuthority>();

        var canonical = JsonSerializer.Serialize(new
        {
            contractVersion = CurrentContractVersion,
            scopeMode,
            tenantRoot = Normalize(tenantRoot),
            sites = Canonical(sites, site => new
            {
                siteId = site.SiteId.ToString("D"),
                rootWebId = site.RootWebId.ToString("D"),
                url = Normalize(site.Url),
                site.GraphId,
                site.Name,
            }),
            webs = siteWebs.OrderBy(item => Normalize(item.Site.Url), StringComparer.Ordinal).Select(item => new
            {
                siteUrl = Normalize(item.Site.Url),
                result = Canonical(item.Webs, web => new
                {
                    webId = web.WebId.ToString("D"),
                    url = Normalize(web.Url),
                    web.ServerRelativeUrl,
                    parentWebUrl = Normalize(web.ParentWebUrl),
                    web.WebTemplateConfiguration,
                    web.IsRootWeb,
                }),
            }),
        }, AspxInventoryRuntime.JsonOptions());
        var authorityHash = DiscoveryHash.Of(canonical);
        var tenantVerified = AspxScopeModes.CanVerifyTenant(scopeMode) && sites.IsTerminalSuccess &&
            !sites.ContinuationRemaining && (sites.Exclusions?.Count ?? 0) == 0 &&
            string.IsNullOrWhiteSpace(sites.FailureCode) &&
            siteWebs.Count == sites.Items.Count && siteWebs.All(item => item.Webs.IsTerminalSuccess &&
                !item.Webs.ContinuationRemaining && (item.Webs.Exclusions?.Count ?? 0) == 0 &&
                string.IsNullOrWhiteSpace(item.Webs.FailureCode));
        return new(CurrentContractVersion, scopeMode, tenantRoot,
            CurrentContractVersion, authorityHash, sites,
            siteWebs.OrderBy(item => Normalize(item.Site.Url), StringComparer.Ordinal).ToArray(), tenantVerified);
    }

    private static object Canonical<T>(AspxAuthorityCollection<T> result, Func<T, object> item) => new
    {
        outcome = result.Outcome.ToString(),
        items = (result.Items ?? Array.Empty<T>()).Select(item).OrderBy(value =>
            JsonSerializer.Serialize(value, AspxInventoryRuntime.JsonOptions()), StringComparer.Ordinal),
        result.Provider,
        result.Operation,
        result.ActualFilter,
        exclusions = (result.Exclusions ?? Array.Empty<string>()).OrderBy(value => value, StringComparer.Ordinal),
        result.FailureCode,
        result.FailureDetail,
        result.ContinuationRemaining,
    };

    internal static string Normalize(Uri value) => value?.AbsoluteUri.TrimEnd('/').ToLowerInvariant();
}

internal interface IAspxTenantAuthorityAdapter
{
    Task<AspxAuthorityCollection<AspxAuthoritySite>> EnumerateSiteCollectionsAsync(
        Uri tenantRoot, CancellationToken cancellationToken = default);
    Task<AspxAuthorityCollection<AspxAuthorityWeb>> EnumerateWebsAsync(
        AspxAuthoritySite site, CancellationToken cancellationToken = default);

    Task<AspxAuthoritySite> ResolveDeclaredSiteAsync(Uri siteUrl,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AspxAuthoritySite(Guid.Empty, Guid.Empty, siteUrl, null, null));
}

internal sealed class PnPCoreAspxTenantAuthorityAdapter : IAspxTenantAuthorityAdapter
{
    internal const string SiteProvider = "PnP.Core.Admin:ISiteCollectionManager";
    internal const string SiteOperation = "GetSiteCollectionsAsync";
    internal const string SiteFilter = "SiteCollectionFilter.Default";
    internal const string WebProvider = "PnP.Core.Admin:ISiteCollectionManager";
    internal const string WebOperation = "IWeb.GetAsync(root identity) + GetSiteCollectionWebsWithDetailsAsync";
    internal const string WebFilter = "skipAppWebs=false";

    private readonly IPnPContextFactory contextFactory;
    private readonly IAuthenticationProvider authenticationProvider;

    internal PnPCoreAspxTenantAuthorityAdapter(IPnPContextFactory contextFactory,
        IAuthenticationProvider authenticationProvider)
    {
        this.contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        this.authenticationProvider = authenticationProvider ?? throw new ArgumentNullException(nameof(authenticationProvider));
    }

    public async Task<AspxAuthorityCollection<AspxAuthoritySite>> EnumerateSiteCollectionsAsync(
        Uri tenantRoot, CancellationToken cancellationToken = default)
    {
        using var context = await contextFactory.CreateAsync(tenantRoot, authenticationProvider,
            cancellationToken, new PnPContextOptions()).ConfigureAwait(false);
        var manager = context.GetSiteCollectionManager();
        try
        {
            var found = await manager.GetSiteCollectionsAsync(
                filter: SiteCollectionFilter.Default).ConfigureAwait(false);
            var sites = MapSites(found);
            var outcome = sites.Length == 0 ? DiscoveryTerminalOutcome.Empty : DiscoveryTerminalOutcome.Complete;
            return new(outcome, sites, SiteProvider, SiteOperation, SiteFilter, Array.Empty<string>(),
                null, null, ContinuationRemaining: false, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception primary)
        {
            try
            {
                var visible = await manager.GetSiteCollectionsAsync(ignoreUserIsSharePointAdmin: true,
                    filter: SiteCollectionFilter.Default).ConfigureAwait(false);
                var sites = MapSites(visible);
                var outcome = sites.Length == 0 ? DiscoveryTerminalOutcome.Empty : DiscoveryTerminalOutcome.Complete;
                return new(outcome, sites, SiteProvider,
                    "GetSiteCollectionsAsync(ignoreUserIsSharePointAdmin=true)", SiteFilter,
                    new[] { "tenant-admin-authority-failed:fallback-user-visible-graph-search" },
                    null, "primary=" + Bounded(primary), ContinuationRemaining: false, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception fallback)
            {
                var outcome = Classify(fallback);
                return new(outcome, Array.Empty<AspxAuthoritySite>(), SiteProvider,
                    "GetSiteCollectionsAsync(Default)+GetSiteCollectionsAsync(ignoreUserIsSharePointAdmin=true)",
                    SiteFilter, Array.Empty<string>(), outcome == DiscoveryTerminalOutcome.Denied
                        ? "tenant_site_authority_denied" : "tenant_site_authority_failed",
                    "primary=" + Bounded(primary) + "; fallback=" + Bounded(fallback),
                    ContinuationRemaining: false, DateTimeOffset.UtcNow);
            }
        }
    }

    private static AspxAuthoritySite[] MapSites(IEnumerable<ISiteCollection> found) =>
        (found ?? Array.Empty<ISiteCollection>()).Where(site => site?.Url != null)
        .Select(site => new AspxAuthoritySite(site.Id, site.RootWebId, site.Url, site.GraphId, site.Name))
        .DistinctBy(site => AspxTenantAuthoritySnapshot.Normalize(site.Url), StringComparer.Ordinal)
        .OrderBy(site => AspxTenantAuthoritySnapshot.Normalize(site.Url), StringComparer.Ordinal)
        .ToArray();

    public async Task<AspxAuthorityCollection<AspxAuthorityWeb>> EnumerateWebsAsync(
        AspxAuthoritySite site, CancellationToken cancellationToken = default)
    {
        var fallbackRoot = new AspxAuthorityWeb(site.RootWebId, site.Url, site.Url.AbsolutePath,
            null, null, IsRootWeb: true);
        try
        {
            using var context = await contextFactory.CreateAsync(site.Url, authenticationProvider,
                cancellationToken, new PnPContextOptions()).ConfigureAwait(false);
            var rootWeb = await context.Web.GetAsync(web => web.Id, web => web.Url,
                web => web.ServerRelativeUrl, web => web.WebTemplateConfiguration).ConfigureAwait(false);
            var root = new AspxAuthorityWeb(rootWeb.Id, rootWeb.Url ?? site.Url,
                rootWeb.ServerRelativeUrl ?? site.Url.AbsolutePath, null,
                rootWeb.WebTemplateConfiguration, IsRootWeb: true);
            var found = await context.GetSiteCollectionManager().GetSiteCollectionWebsWithDetailsAsync(
                site.Url, skipAppWebs: false).ConfigureAwait(false);
            var candidates = found.Where(web => web?.Url != null)
                .Select(web => new AspxAuthorityWeb(web.Id, web.Url, web.ServerRelativeUrl, null,
                    web.WebTemplateConfiguration, IsRootWeb: false))
                .Append(root)
                .DistinctBy(web => AspxTenantAuthoritySnapshot.Normalize(web.Url), StringComparer.Ordinal)
                .OrderBy(web => AspxTenantAuthoritySnapshot.Normalize(web.Url), StringComparer.Ordinal)
                .ToArray();
            var withParents = candidates.Select(web => web.IsRootWeb ? web : web with
            {
                ParentWebUrl = FindParent(web.Url, candidates.Select(item => item.Url)),
            }).ToArray();
            var missingParent = withParents.Where(web => !web.IsRootWeb && web.ParentWebUrl == null).ToArray();
            if (missingParent.Length > 0)
                return new(DiscoveryTerminalOutcome.Unknown, withParents, WebProvider, WebOperation, WebFilter,
                    Array.Empty<string>(), "subweb_parent_linkage_unknown",
                    $"{missingParent.Length} subweb(s) had no direct parent in the returned site authority set.",
                    ContinuationRemaining: false, DateTimeOffset.UtcNow);
            return new(withParents.Length == 0 ? DiscoveryTerminalOutcome.Empty : DiscoveryTerminalOutcome.Complete,
                withParents, WebProvider, WebOperation, WebFilter, Array.Empty<string>(), null, null,
                ContinuationRemaining: false, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var outcome = Classify(ex);
            return new(outcome, new[] { fallbackRoot }, WebProvider, WebOperation, WebFilter, Array.Empty<string>(),
                outcome == DiscoveryTerminalOutcome.Denied
                    ? "root_and_subweb_authority_denied" : "root_and_subweb_authority_failed",
                Bounded(ex), ContinuationRemaining: false, DateTimeOffset.UtcNow);
        }
    }

    public async Task<AspxAuthoritySite> ResolveDeclaredSiteAsync(Uri siteUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(siteUrl);
        using var context = await contextFactory.CreateAsync(siteUrl, authenticationProvider,
            cancellationToken, new PnPContextOptions()).ConfigureAwait(false);
        var site = await context.Site.GetAsync(value => value.Id).ConfigureAwait(false);
        var rootWeb = await context.Web.GetAsync(value => value.Id, value => value.Url,
            value => value.Title).ConfigureAwait(false);
        return new AspxAuthoritySite(site.Id, rootWeb.Id, rootWeb.Url ?? siteUrl, null, rootWeb.Title);
    }

    private static Uri FindParent(Uri child, IEnumerable<Uri> candidates)
    {
        var childPath = child.AbsolutePath.TrimEnd('/');
        return candidates.Where(candidate => candidate != null &&
                string.Equals(candidate.Host, child.Host, StringComparison.OrdinalIgnoreCase))
            .Where(candidate =>
            {
                var parentPath = candidate.AbsolutePath.TrimEnd('/');
                return parentPath.Length < childPath.Length &&
                    (parentPath.Length == 0 || childPath.StartsWith(parentPath + "/", StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(candidate => candidate.AbsolutePath.TrimEnd('/').Length)
            .FirstOrDefault();
    }

    private static DiscoveryTerminalOutcome Classify(Exception ex) =>
        ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            ? DiscoveryTerminalOutcome.Denied : DiscoveryTerminalOutcome.Failed;

    private static string Bounded(Exception ex)
    {
        var value = ex is ServiceException { Error: ServiceError error }
            ? string.Join("; ", new[]
            {
                ex.GetType().Name,
                $"http={error.HttpResponseCode}",
                $"code={error.Code ?? "unknown"}",
                $"message={error.Message ?? ex.Message}",
                $"requestId={error.ClientRequestId ?? "unknown"}",
            })
            : ex.GetType().Name + ": " + ex.Message;
        return value[..Math.Min(value.Length, 1024)];
    }
}

internal static class AspxTenantAuthorityCapture
{
    private const int WebAuthorityMaxConcurrency = 4;
    private static readonly TimeSpan DefaultWebAuthorityTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<AspxTenantAuthoritySnapshot> CaptureAsync(string scopeMode, Uri tenantRoot,
        IReadOnlyList<Uri> declaredSites, IAspxTenantAuthorityAdapter adapter,
        CancellationToken cancellationToken = default, TimeSpan? webAuthorityTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        AspxAuthorityCollection<AspxAuthoritySite> sites;
        if (scopeMode == AspxScopeModes.ProductTenantAuthority)
        {
            sites = await adapter.EnumerateSiteCollectionsAsync(tenantRoot, cancellationToken).ConfigureAwait(false);
        }
        else if (scopeMode == AspxScopeModes.DeclaredSubset)
        {
            var declaredUrls = (declaredSites ?? Array.Empty<Uri>())
                .DistinctBy(AspxTenantAuthoritySnapshot.Normalize, StringComparer.Ordinal)
                .OrderBy(AspxTenantAuthoritySnapshot.Normalize, StringComparer.Ordinal)
                .ToArray();
            var declared = new List<AspxAuthoritySite>();
            var identityFailures = new List<string>();
            foreach (var url in declaredUrls)
            {
                try
                {
                    declared.Add(await adapter.ResolveDeclaredSiteAsync(url, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    declared.Add(new AspxAuthoritySite(Guid.Empty, Guid.Empty, url, null, null));
                    identityFailures.Add($"{AspxTenantAuthoritySnapshot.Normalize(url)}:{ex.GetType().Name}");
                }
            }
            sites = new(declared.Count == 0 ? DiscoveryTerminalOutcome.Empty :
                    identityFailures.Count == 0 ? DiscoveryTerminalOutcome.Complete : DiscoveryTerminalOutcome.Failed,
                declared, "Assessment.CLI+PnP.Core", "DeclaredSiteArguments+ResolveDeclaredSiteAsync", "--site", new[]
                {
                    "tenant-site-denominator-not-enumerated",
                }, identityFailures.Count == 0 ? null : "declared_site_identity_failed",
                identityFailures.Count == 0 ? null : string.Join(';', identityFailures),
                ContinuationRemaining: false, DateTimeOffset.UtcNow);
        }
        else
        {
            throw new InvalidOperationException(
                $"Live acquisition supports only '{AspxScopeModes.ProductTenantAuthority}' or '{AspxScopeModes.DeclaredSubset}'.");
        }

        var siteWebs = new AspxSiteWebAuthority[sites.Items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, sites.Items.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = WebAuthorityMaxConcurrency,
            CancellationToken = cancellationToken,
        }, async (index, token) =>
        {
            var site = sites.Items[index];
            using var siteCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            siteCancellation.CancelAfter(webAuthorityTimeout ?? DefaultWebAuthorityTimeout);
            try
            {
                siteWebs[index] = new(site,
                    await adapter.EnumerateWebsAsync(site, siteCancellation.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && siteCancellation.IsCancellationRequested)
            {
                var root = new AspxAuthorityWeb(site.RootWebId, site.Url, site.Url.AbsolutePath,
                    null, null, IsRootWeb: true);
                siteWebs[index] = new(site, new AspxAuthorityCollection<AspxAuthorityWeb>(
                    DiscoveryTerminalOutcome.Failed, new[] { root },
                    PnPCoreAspxTenantAuthorityAdapter.WebProvider,
                    PnPCoreAspxTenantAuthorityAdapter.WebOperation,
                    $"{PnPCoreAspxTenantAuthorityAdapter.WebFilter};timeout={(webAuthorityTimeout ?? DefaultWebAuthorityTimeout).TotalSeconds:0}s",
                    Array.Empty<string>(), "site_web_authority_timeout",
                    $"Web authority enumeration exceeded {(webAuthorityTimeout ?? DefaultWebAuthorityTimeout).TotalSeconds:0} seconds; the known root remains scannable.",
                    ContinuationRemaining: false, DateTimeOffset.UtcNow));
            }
        }).ConfigureAwait(false);
        return AspxTenantAuthoritySnapshot.Freeze(scopeMode, tenantRoot, sites, siteWebs);
    }
}
