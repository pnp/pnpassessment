using Microsoft.SharePoint.Client;
using PnP.Core.Services;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal static class ExtensibilityScanComponent
    {
        
        internal static async Task ExecuteAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext)
        {
            List<ClassicExtensibility> classicExtensibilitiesList = new();
            
            if (classicExtensibilitiesList.Count > 0)
            {
                await scannerBase.StorageManager.StoreClassicExtensibilityInformationAsync(scannerBase.ScanId, classicExtensibilitiesList);
            }
        }

    }
}
