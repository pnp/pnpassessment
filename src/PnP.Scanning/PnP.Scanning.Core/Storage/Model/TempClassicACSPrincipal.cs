using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(AppIdentifier), nameof(ServerRelativeUrl) }, IsUnique = true)]
    internal class TempClassicACSPrincipal 
    {
        public Guid ScanId { get; set; }

        /// <summary>
        /// Identifier of the legacy principal
        /// </summary>
        public string AppIdentifier { get; set; }

        /// <summary>
        /// The server relative url of the <see cref="IWeb"/> where the legacy principal is located
        /// </summary>
        public string ServerRelativeUrl { get; set; }

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
