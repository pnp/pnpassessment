using Microsoft.EntityFrameworkCore;
using Microsoft.SharePoint.Client.WebParts;

namespace PnP.Scanning.Core.Storage;

[Index(nameof(ScanId), [nameof(SiteUrl), nameof(WebUrl), nameof(PageUrl)], IsUnique = false)]
internal class ClassicWebParts : BaseScanResult
{
    public string PageUrl { get; set; }

    public string PageName { get; set; }
        
    public string PageType { get; set; }
        
    public string ListUrl { get; set; }

    public string ListTitle { get; set; }
    
    public string ControlId { get; set; }
    
    public string WebPartType { get; set; }

    public string HasMapping { get; set; }
}