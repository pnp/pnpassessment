using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(AppIdentifier) }, IsUnique = true)]
    internal class TempClassicACSPrincipalValidUntil
    {
        public Guid ScanId { get; set; }

        /// <summary>
        /// Identifier of the legacy principal
        /// </summary>
        public string AppIdentifier { get; set; }

        /// <summary>
        /// Principal is valid until. This value is only populated when using the ISiteCollectionManager.GetTenantAndSiteCollectionACSPrincipalsAsync method
        /// </summary>
        public DateTime ValidUntil { get; set; }
    }
}
