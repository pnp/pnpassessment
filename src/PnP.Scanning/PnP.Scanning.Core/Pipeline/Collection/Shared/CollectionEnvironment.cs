using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Discovery;
#nullable enable
using Microsoft.AspNetCore.DataProtection;
using PnP.Core.Services;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Pipeline.Collection.Shared;

/// <summary>
/// Groups authenticated context creation and shared site enumeration for one collection.
/// </summary>
internal sealed record CollectionServices(
    AuthenticationManager Authentication, IPnPContextFactory ContextFactory,
    SiteEnumerationManager SiteEnumeration);

/// <summary>
/// Defers authentication and online service resolution until collection requests them.
/// </summary>
internal sealed class CollectionEnvironment(
    Func<IDataProtectionProvider> protectionProvider,
    Func<IPnPContextFactory> contextFactory,
    Func<SiteEnumerationManager> siteEnumeration) : ICollectionEnvironment
{
    /// <summary>
    /// Clones collection options and protects secrets before persistence.
    /// </summary>
    public StartRequest Protect(StartRequest options)
    {
        StartRequest? copy = options.Clone();
        if (!string.IsNullOrEmpty(copy.CertPassword))
            copy.CertPassword = protectionProvider().CreateProtector("Pipeline.Collection.Configuration.v1").Protect(copy.CertPassword);
        return copy;
    }

    /// <summary>
    /// Restores protected collection options only when online collection resumes.
    /// </summary>
    public StartRequest Restore(StartRequest options)
    {
        StartRequest? copy = options.Clone();
        if (!string.IsNullOrEmpty(copy.CertPassword))
            copy.CertPassword = protectionProvider().CreateProtector("Pipeline.Collection.Configuration.v1").Unprotect(copy.CertPassword);
        return copy;
    }

    /// <summary>
    /// Initializes authentication and resolves online services only for collection execution.
    /// </summary>
    public async Task<CollectionServices> OpenAsync(StartRequest options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartRequest? copy = options.Clone();
        if (string.IsNullOrWhiteSpace(copy.TenantId) && !string.IsNullOrWhiteSpace(copy.Tenant))
            copy.TenantId = (await AuthenticationManager.GetAzureADTenantIdAsync(copy.Tenant)).ToString();
        cancellationToken.ThrowIfCancellationRequested();
        AuthenticationManager? authentication = AuthenticationManager.Create(copy, protectionProvider());
        authentication.SetCollectionDeviceCodeCallback(result =>
        {
            Serilog.Log.Information("Collection authentication: {Message}", result.Message);
            return Task.CompletedTask;
        });
        return new(authentication, contextFactory(), siteEnumeration());
    }
}
