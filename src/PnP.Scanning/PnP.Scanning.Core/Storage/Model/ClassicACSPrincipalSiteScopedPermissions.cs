using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(AppIdentifier), nameof(ServerRelativeUrl), nameof(SiteId), nameof(WebId), nameof(ListId), nameof(Right) }, IsUnique = true)]
    internal class ClassicACSPrincipalSiteScopedPermissions
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

        public Guid SiteId { get; set; }

        public Guid WebId { get; set; }

        public Guid ListId { get; set; }

        public string Right { get; set; }

        public string RemediationCode { get; set; }
    }
}
