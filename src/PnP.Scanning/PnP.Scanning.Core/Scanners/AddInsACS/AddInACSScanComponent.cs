using Microsoft.SharePoint.Client;
using PnP.Core.Admin.Model.SharePoint;
using PnP.Core.Services;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal static class AddInACSScanComponent
    {
        private static readonly Guid SharePointPrincipal = Guid.Parse("00000003-0000-0FF1-CE00-000000000000");

        internal static async Task ExecuteAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext, VanityUrlOptions vanityUrlOptions)
        {
            var addIns = await context.GetSiteCollectionManager().GetSiteCollectionSharePointAddInsAsync(false, vanityUrlOptions);
            var principals = await context.GetSiteCollectionManager().GetSiteCollectionACSPrincipalsAsync(false, vanityUrlOptions);

            List<ClassicAddIn> classicAddIns = new();
            foreach (var addIn in addIns)
            {
                classicAddIns.Add(new ClassicAddIn
                {
                    ScanId = scannerBase.ScanId,
                    SiteUrl = scannerBase.SiteUrl,
                    WebUrl = scannerBase.WebUrl,
                    AppIdentifier = addIn.AppIdentifier,
                    Title = addIn.Title,
                    Type = DetermineType(addIn),
                    AppInstanceId = addIn.AppInstanceId,
                    AppWebFullUrl = addIn.AppWebFullUrl,
                    AppSource = addIn.AppSource.ToString(),
                    AppWebId = addIn.AppWebId,
                    AssetId = addIn.AssetId,
                    CreationTime = addIn.CreationTime,
                    CurrentSiteId = addIn.CurrentSiteId,
                    CurrentWebFullUrl = addIn.CurrentWebFullUrl,
                    CurrentWebId = addIn.CurrentWebId,
                    CurrentWebName = addIn.CurrentWebName,
                    InstalledBy = addIn.InstalledBy,
                    InstalledSiteId = addIn.InstalledSiteId,
                    InstalledWebFullUrl = addIn.InstalledWebFullUrl,
                    InstalledWebId = addIn.InstalledWebId,
                    InstalledWebName = addIn.InstalledWebName,
                    LaunchUrl = addIn.LaunchUrl,
                    LicensePurchaseTime = addIn.LicensePurchaseTime,
                    Locale = addIn.Locale,
                    ProductId = addIn.ProductId,
                    PurchaserIdentity = addIn.PurchaserIdentity,
                    Status = addIn.Status.ToString(),
                    TenantAppData = addIn.TenantAppData,
                    TenantAppDataUpdateTime = addIn.TenantAppDataUpdateTime,
                    RemediationCode = "",
                });
            }

            // Save the add-in data
            await scannerBase.StorageManager.StoreSharePointAddInInformationAsync(scannerBase.ScanId, classicAddIns);

            List<TempClassicACSPrincipal> classicACSPrincipals = new();
            List<ClassicACSPrincipalSiteScopedPermissions> siteScopedPermissions = new();
            List<ClassicACSPrincipalTenantScopedPermissions> tenantScopedPermissions = new();

            foreach (var principal in principals)
            {

                if (principal.AppId == SharePointPrincipal)
                {
                    // Skip the SharePoint principal
                    continue;
                }

                if (string.Join(",", principal.AppDomains).Contains("workflow.windows.net", StringComparison.InvariantCultureIgnoreCase))
                {
                    // Skip SharePoint 2013 Workflow principals
                    continue;
                }                

                classicACSPrincipals.Add(new TempClassicACSPrincipal
                {
                    ScanId = scannerBase.ScanId,
                    AppIdentifier = principal.AppIdentifier,
                    ServerRelativeUrl = principal.ServerRelativeUrl,
                    Title = principal.Title,
                    AllowAppOnly = principal.AllowAppOnly,
                    AppId = principal.AppId,
                    RedirectUri = principal.RedirectUri,
                    ValidUntil = principal.ValidUntil,
                    AppDomains = string.Join(",", principal.AppDomains),
                    RemediationCode = "",
                });

                PopulatePrincipalPermissions(scannerBase, siteScopedPermissions, tenantScopedPermissions, principal);
            }

            // Save the Azure ACS data in the database
            await scannerBase.StorageManager.StoreAzureACSInformationAsync(scannerBase.ScanId, null, classicACSPrincipals, siteScopedPermissions, tenantScopedPermissions);
        }

        private static string DetermineType(ISharePointAddIn addIn)
        {
            string type = "Unknown";

            if (!string.IsNullOrEmpty(addIn.AppWebFullUrl))
            {
                // There's an app web, so there's also a SharePoint hosted component. Check if there's also an ACS principal
                if (addIn.AppIdentifier.Contains("|ms.sp.ext|", StringComparison.InvariantCultureIgnoreCase))
                {
                    type = "Hybrid";
                }
                else
                {
                    type = "SharePoint hosted";
                }
            }
            else
            {
                if (addIn.AppIdentifier.Contains("|ms.sp.ext|", StringComparison.InvariantCultureIgnoreCase))
                {
                    type = "Provider hosted";
                }
            }

            return type;
        }

        internal static async Task ExecutePreScanningAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext, VanityUrlOptions vanityUrlOptions)
        {
            // Load the tenant scoped principals
            var legacyServicePrincipals = await context.GetSiteCollectionManager().GetLegacyServicePrincipalsAsync(true);
            var principals = await context.GetSiteCollectionManager().GetTenantACSPrincipalsAsync(legacyServicePrincipals, vanityUrlOptions);

            List<TempClassicACSPrincipal> classicACSPrincipals = new();
            List<TempClassicACSPrincipalValidUntil> classicACSPrincipalValidUntils = new();
            List<ClassicACSPrincipalSiteScopedPermissions> siteScopedPermissions = new();
            List<ClassicACSPrincipalTenantScopedPermissions> tenantScopedPermissions = new();

            foreach (var legacyServicePrincipal in legacyServicePrincipals)
            {
                classicACSPrincipalValidUntils.Add(new TempClassicACSPrincipalValidUntil
                {
                    ScanId = scannerBase.ScanId,
                    AppIdentifier = legacyServicePrincipal.AppIdentifier,
                    ValidUntil = legacyServicePrincipal.ValidUntil
                });
            }

            foreach (var principal in principals)
            {
                //As we ran this for the first site collection the used server relative url is that site while this principal applies to the whole tenant,
                //therefore replace with a generic url
                classicACSPrincipals.Add(new TempClassicACSPrincipal
                {
                    ScanId = scannerBase.ScanId,
                    AppIdentifier = principal.AppIdentifier,
                    ServerRelativeUrl = "<tenant>",
                    Title = principal.Title,
                    AllowAppOnly = principal.AllowAppOnly,
                    AppId = principal.AppId,
                    RedirectUri = principal.RedirectUri,
                    ValidUntil = principal.ValidUntil,
                    AppDomains = string.Join(",", principal.AppDomains),
                    RemediationCode = "",
                });

                PopulatePrincipalPermissions(scannerBase, siteScopedPermissions, tenantScopedPermissions, principal);
            }

            // Save the data in the database
            await scannerBase.StorageManager.StoreAzureACSInformationAsync(scannerBase.ScanId, classicACSPrincipalValidUntils, classicACSPrincipals, siteScopedPermissions, tenantScopedPermissions);
        }

        internal static async Task ExecutePostScanningAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext)
        {
            await scannerBase.StorageManager.UpdateACSPrincipalInformationAsync(scannerBase.ScanId);
        }

        private static void PopulatePrincipalPermissions(ScannerBase scannerBase, List<ClassicACSPrincipalSiteScopedPermissions> siteScopedPermissions, List<ClassicACSPrincipalTenantScopedPermissions> tenantScopedPermissions, ILegacyPrincipal principal)
        {
            foreach (var siteCollectionPermission in principal.SiteCollectionScopedPermissions)
            {
                siteScopedPermissions.Add(new ClassicACSPrincipalSiteScopedPermissions
                {
                    ScanId = scannerBase.ScanId,
                    AppIdentifier = principal.AppIdentifier,
                    ServerRelativeUrl = principal.ServerRelativeUrl,
                    SiteId = siteCollectionPermission.SiteId,
                    WebId = siteCollectionPermission.WebId,
                    ListId = siteCollectionPermission.ListId,
                    Right = siteCollectionPermission.Right.ToString(),
                    RemediationCode = "",
                });
            }

            foreach (var tenantPermissions in principal.TenantScopedPermissions)
            {
                tenantScopedPermissions.Add(new ClassicACSPrincipalTenantScopedPermissions
                {
                    ScanId = scannerBase.ScanId,
                    AppIdentifier = principal.AppIdentifier,
                    ProductFeature = tenantPermissions.ProductFeature,
                    Right = tenantPermissions.Right.ToString(),
                    ResourceId = tenantPermissions.ResourceId,
                    Scope = tenantPermissions.Scope,
                    RemediationCode = "",
                });
            }
        }
    }
}
