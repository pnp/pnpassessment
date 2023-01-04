using Microsoft.SharePoint.Client;
using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal static class ExtensibilityScanComponent
    {
        // OOB master pages
        private static readonly List<string> excludeMasterPage = new()
        {
                                                            "v4.master",
                                                            "minimal.master",
                                                            "seattle.master",
                                                            "oslo.master",
                                                            "default.master",
                                                            "app.master",
                                                            "mwsdefault.master",
                                                            "mwsdefaultv4.master",
                                                            "mwsdefaultv15.master",
                                                            "mysite15.master", // mysite host
                                                            "boston.master" // modern group sites
                                                        };

        internal static async Task ExecuteAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext)
        {
            List<ClassicExtensibility> classicExtensibilitiesList = new();
            HashSet<string> remediationCodes = new();
            int classicExtensibilities = 0;

            var classicExtensibility = new ClassicExtensibility
            {
                ScanId = scannerBase.ScanId,
                SiteUrl = scannerBase.SiteUrl,
                WebUrl = scannerBase.WebUrl,
            };

            // Get information about the master pages used
            if (!string.IsNullOrEmpty(context.Web.MasterUrl) && !excludeMasterPage.Contains(context.Web.MasterUrl.Substring(context.Web.MasterUrl.LastIndexOf("/") + 1).ToLower()))
            {
                classicExtensibility.MasterPage = context.Web.MasterUrl;
            }
            
            if (!string.IsNullOrEmpty(context.Web.CustomMasterUrl) && !excludeMasterPage.Contains(context.Web.CustomMasterUrl.Substring(context.Web.CustomMasterUrl.LastIndexOf("/") + 1).ToLower()))
            {
                classicExtensibility.CustomMasterPage = context.Web.CustomMasterUrl;
            }

            if (!string.IsNullOrEmpty(classicExtensibility.MasterPage) || !string.IsNullOrEmpty(classicExtensibility.CustomMasterPage))
            {
                classicExtensibility.UsesCustomMasterPage = true;
                classicExtensibilities++;
                remediationCodes.Add(RemediationCodes.CE3.ToString());
            }

            if (!string.IsNullOrEmpty(context.Web.AlternateCssUrl))
            {
                classicExtensibility.AlternateCSS = context.Web.AlternateCssUrl;
                classicExtensibility.UsesCustomCSS = true;
                classicExtensibilities++;
                remediationCodes.Add(RemediationCodes.CE4.ToString());
            }

            using (var dbContext = new ScanContext(scannerBase.ScanId))
            {
                foreach (var userCustomAction in dbContext.ClassicUserCustomActions.Where(p => p.ScanId == scannerBase.ScanId && p.SiteUrl == scannerBase.SiteUrl && p.WebUrl == scannerBase.WebUrl))
                {
                    classicExtensibility.UsesUserCustomAction = true;
                    classicExtensibilities++;
                    remediationCodes.Add(userCustomAction.RemediationCode);
                }
            }

            if (classicExtensibilities > 0)
            {
                classicExtensibility.RemediationCode = string.Join(",", remediationCodes);
                classicExtensibilitiesList.Add(classicExtensibility);
            }

            if (classicExtensibilitiesList.Count > 0)
            {
                await scannerBase.StorageManager.StoreClassicExtensibilityInformationAsync(scannerBase.ScanId, classicExtensibilitiesList);
            }

            if (classicExtensibilities > 0)
            {
                await scannerBase.StorageManager.StoreExtensibilitySummaryAsync(scannerBase.ScanId, scannerBase.SiteUrl, scannerBase.WebUrl, scannerBase.WebTemplate, context, remediationCodes, classicExtensibilities);
            }
        }

    }
}
