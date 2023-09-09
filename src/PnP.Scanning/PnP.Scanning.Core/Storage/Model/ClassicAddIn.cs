using Microsoft.EntityFrameworkCore;

namespace PnP.Scanning.Core.Storage
{
    [Index(new string[] { nameof(ScanId), nameof(SiteUrl), nameof(WebUrl), nameof(AppInstanceId) }, IsUnique = true)]
    internal class ClassicAddIn : BaseScanResult
    {
        /// <summary>
        /// Instance ID of this app
        /// </summary>
        public Guid AppInstanceId { get; set; }

        public string Title { get; set; }

        public string Type { get; set; }

        public bool HasExpired { get; set; }

        /// <summary>
        /// The source of this addin
        /// </summary>
        public string AppSource { get; set; }

        /// <summary>
        /// The full URL of the app web created by the addin
        /// </summary>
        public string AppWebFullUrl { get; set; }

        /// <summary>
        /// Id of the app web created by the addin
        /// </summary>
        public Guid AppWebId { get; set; }

        /// <summary>
        /// The id of the app in the office store, this will be empty for user uploaded apps
        /// </summary>
        public string AssetId { get; set; }

        /// <summary>
        /// Date and time when the addin was installed
        /// </summary>
        public DateTime CreationTime { get; set; }

        /// <summary>
        /// Name of the user who installed the addin
        /// </summary>
        public string InstalledBy { get; set; }

        /// <summary>
        /// Id of the site collection where the addin actually is installed. This can be different from the site collection where the addin was listed as being available
        /// </summary>
        public Guid InstalledSiteId { get; set; }

        /// <summary>
        /// Id of the web where the addin actually is installed. This can be different from the web where the addin was listed as being available
        /// </summary>
        public Guid InstalledWebId { get; set; }

        /// <summary>
        /// Fully qualified URL of the web where the addin actually is installed. This can be different from the web where the addin was listed as being available
        /// </summary>
        public string InstalledWebFullUrl { get; set; }

        /// <summary>
        /// Name of the web where the addin actually is installed. This can be different from the web where the addin was listed as being available
        /// </summary>
        public string InstalledWebName { get; set; }

        /// <summary>
        /// Id of the site collection where the addin actually is listed for
        /// </summary>
        public Guid CurrentSiteId { get; set; }

        /// <summary>
        /// Id of the web where the addin actually is listed for
        /// </summary>
        public Guid CurrentWebId { get; set; }

        /// <summary>
        /// Fully qualified URL of the web where the addin actually is listed for
        /// </summary>
        public string CurrentWebFullUrl { get; set; }

        /// <summary>
        /// Name of the web where the addin actually is listed for
        /// </summary>
        public string CurrentWebName { get; set; }

        /// <summary>
        /// Where to redirect after clicking on the add-in (e.g. ~appWebUrl/Pages/Default.aspx?{StandardTokens})
        /// </summary>
        public string LaunchUrl { get; set; }

        /// <summary>
        /// When was the app license purchased
        /// </summary>
        public DateTime LicensePurchaseTime { get; set; }

        /// <summary>
        /// Identity of the user that purchased the app
        /// </summary>
        public string PurchaserIdentity { get; set; }

        /// <summary>
        /// Locale used by the web where the add-in is installed
        /// </summary>
        public string Locale { get; set; }

        /// <summary>
        /// The global unique id of the add-in. It is same for all tenants
        /// </summary>
        public Guid ProductId { get; set; }

        /// <summary>
        /// Status of the addin
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// After the add-in installed in the tenant app catalog site. It could enable tenant level usage. This data indicates the tenant the conditions how to filter the webs. 
        /// See https://learn.microsoft.com/en-us/sharepoint/dev/sp-add-ins/tenancies-and-deployment-scopes-for-sharepoint-add-ins for more details
        /// </summary>
        public string TenantAppData { get; set; }

        /// <summary>
        /// When was the <see cref="TenantAppData"/> last updated?
        /// </summary>
        public DateTime TenantAppDataUpdateTime { get; set; }

        #region ILegacyPrincipal properties
        /// <summary>
        /// Identifier of the legacy principal
        /// </summary>
        public string AppIdentifier { get; set; }

        public string RemediationCode { get; set; }
        
        #endregion
    }
}
