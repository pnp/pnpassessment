using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(nameof(ScanId), [nameof(AppIdentifier), nameof(ProductFeature), nameof(Scope), nameof(Right), nameof(ResourceId)], IsUnique = true)]
    internal class ClassicACSPrincipalTenantScopedPermissions
    {
        public Guid ScanId { get; set; }

        /// <summary>
        /// Identifier of the legacy principal
        /// </summary>
        public string AppIdentifier { get; set; }

        /// <summary>
        /// The feature name of the permissions (Taxonomy/ Social/ ProjectServer/ Search/ BcsConnection/ Content)
        /// </summary>
        public string ProductFeature { get; set; }

        /// <summary>
        /// The scope of the permission. E.g. content/tenant or projectserver/projects
        /// </summary>
        public string Scope { get; set; }

        /// <summary>
        /// The granted right
        /// </summary>
        public string Right { get; set; }

        /// <summary>
        /// The specific resource id given to the app. For example, if the permission given to the specific project server, then this is the project server id. 
        /// </summary>
        public string ResourceId { get; set; }

        public string RemediationCode { get; set; }
    }
}
