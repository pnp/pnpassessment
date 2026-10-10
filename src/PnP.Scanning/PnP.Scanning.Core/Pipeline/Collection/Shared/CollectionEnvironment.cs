#nullable enable
using Microsoft.AspNetCore.DataProtection;
using PnP.Core.Services;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Pipeline.Collection.Shared;

internal sealed record CollectionServices(
    AuthenticationManager Authentication, IPnPContextFactory ContextFactory,
    SiteEnumerationManager SiteEnumeration);

/// <summary>All service resolution is deferred until a collector explicitly requests online access.</summary>
internal sealed class CollectionEnvironment(
    Func<IDataProtectionProvider> protectionProvider,
    Func<IPnPContextFactory> contextFactory,
    Func<SiteEnumerationManager> siteEnumeration) : ICollectionEnvironment
{
    public StartRequest Protect(StartRequest options)
    {
        var copy = options.Clone();
        if (!string.IsNullOrEmpty(copy.CertPassword))
            copy.CertPassword = protectionProvider().CreateProtector("Pipeline.Collection.Configuration.v1").Protect(copy.CertPassword);
        return copy;
    }

    public StartRequest Restore(StartRequest options)
    {
        var copy = options.Clone();
        if (!string.IsNullOrEmpty(copy.CertPassword))
            copy.CertPassword = protectionProvider().CreateProtector("Pipeline.Collection.Configuration.v1").Unprotect(copy.CertPassword);
        return copy;
    }

    public async Task<CollectionServices> OpenAsync(StartRequest options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copy = options.Clone();
        if (string.IsNullOrWhiteSpace(copy.TenantId) && !string.IsNullOrWhiteSpace(copy.Tenant))
            copy.TenantId = (await AuthenticationManager.GetAzureADTenantIdAsync(copy.Tenant)).ToString();
        cancellationToken.ThrowIfCancellationRequested();
        var authentication = AuthenticationManager.Create(copy, protectionProvider());
        authentication.SetCollectionDeviceCodeCallback(result =>
        {
            Serilog.Log.Information("Collection authentication: {Message}", result.Message);
            return Task.CompletedTask;
        });
        return new(authentication, contextFactory(), siteEnumeration());
    }
}
