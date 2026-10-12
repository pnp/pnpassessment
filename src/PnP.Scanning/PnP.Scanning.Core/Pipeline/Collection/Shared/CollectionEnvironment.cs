#nullable enable
using Microsoft.AspNetCore.DataProtection;
using PnP.Core.Services;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Authentication;
using PnP.Scanning.Core.Services;

namespace PnP.Scanning.Core.Pipeline.Collection.Shared;

/// <summary>
/// Bundles authentication, PnP context creation and site enumeration for one collection execution.
/// </summary>
internal sealed record CollectionServices(
    AuthenticationManager Authentication, IPnPContextFactory ContextFactory,
    SiteEnumerationManager SiteEnumeration);

/// <summary>
/// Defers service resolution and authentication until a collector requests online access.
/// </summary>
internal sealed class CollectionEnvironment(
    Func<IDataProtectionProvider> protectionProvider,
    Func<IPnPContextFactory> contextFactory,
    Func<SiteEnumerationManager> siteEnumeration) : ICollectionEnvironment
{
    /// <summary>
    /// Clones collection options and encrypts the certificate password before options are persisted.
    /// </summary>
    public StartRequest Protect(StartRequest options)
    {
        var copy = options.Clone();
        if (!string.IsNullOrEmpty(copy.CertPassword))
            copy.CertPassword = protectionProvider().CreateProtector("Pipeline.Collection.Configuration.v1").Protect(copy.CertPassword);
        return copy;
    }

    /// <summary>
    /// Clones saved collection options and decrypts the certificate password for collection resume.
    /// </summary>
    public StartRequest Restore(StartRequest options)
    {
        var copy = options.Clone();
        if (!string.IsNullOrEmpty(copy.CertPassword))
            copy.CertPassword = protectionProvider().CreateProtector("Pipeline.Collection.Configuration.v1").Unprotect(copy.CertPassword);
        return copy;
    }

    /// <summary>
    /// Resolves the tenant identity, initializes authentication and returns the online services for the collector.
    /// <para>Evolved from <c>Services/Scanner.cs</c> to defer tenant and authentication initialization to collection so analysis stays offline.</para>
    /// </summary>
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
