using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(nameof(ScanId), [nameof(AppIdentifier)], IsUnique = true)]
    internal class ClassicACSPrincipal
    {
        public Guid ScanId { get; set; }

        /// <summary>
        /// Identifier of the legacy principal
        /// </summary>
        public string AppIdentifier { get; set; }

        public bool HasExpired { get; set; }

        public bool HasTenantScopedPermissions { get; set; }

        public bool HasSiteCollectionScopedPermissions { get; set; }

        /// <summary>
        /// Title of the legacy principal
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Can this legacy principal use app-only (application permissions)?
        /// </summary>
        public bool AllowAppOnly { get; set; }

        /// <summary>
        /// Id of the app in Azure AD
        /// </summary>
        public Guid AppId { get; set; }

        /// <summary>
        /// Redirect URI used by the app
        /// </summary>
        public string RedirectUri { get; set; }

        /// <summary>
        /// App domains used by the app
        /// </summary>
        public string AppDomains { get; set; }

        /// <summary>
        /// Principal is valid until. This value is only populated when using the ISiteCollectionManager.GetTenantAndSiteCollectionACSPrincipalsAsync method
        /// </summary>
        public DateTime ValidUntil { get; set; }

        public string RemediationCode { get; set; }
    }
}
