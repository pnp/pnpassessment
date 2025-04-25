using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace PnP.Scanning.Core.Storage
{
    [Table("classicACSPrincipalSites")]
    [Index(nameof(ScanId), [nameof(AppIdentifier), nameof(ServerRelativeUrl)], IsUnique = true)]
    internal class ClassicACSPrincipalSite
    {
        public Guid ScanId { get; set; }

        /// <summary>
        /// Identifier of the legacy principal
        /// </summary>
        public string AppIdentifier { get; set; }

        public string ServerRelativeUrl { get; set; }


    }
}
