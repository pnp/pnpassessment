using Microsoft.SharePoint.Client;
using PnP.Core;
using PnP.Core.Services;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal static class ListScanComponent
    {
        
        internal static async Task ExecuteAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext)
        {
            List<ClassicList> classicLists = new();
            
            var lists = ScannerBase.CleanLoadedLists(context);

            foreach (var list in lists)
            {
                var listToAdd = new ClassicList
                {
                    ScanId = scannerBase.ScanId,
                    SiteUrl = scannerBase.SiteUrl,
                    WebUrl = scannerBase.WebUrl,
                    ListUrl = list.RootFolder.ServerRelativeUrl,
                    ListTitle = list.Title,
                    ListId = list.Id,
                    ListTemplateType = list.TemplateType.ToString(),
                    ListTemplate = $"{(int)list.TemplateType}",
                    ListExperience = list.ListExperience.ToString(),
                    ClassicByDesign = IsClassicByDesign(list),
                    LastModifiedAt = list.LastItemUserModifiedDate,
                    ItemCount = list.ItemCount,
                    DefaultViewRenderType = PnP.Core.Model.SharePoint.ListPageRenderType.Undefined.ToString(),
                };

                if (!listToAdd.ClassicByDesign)
                {
                    // Let's ask SharePoint is this list can present itself as modern, if not then the DefaultViewRenderType will tell why
                    try
                    {
                        var defaultViewPage = await context.Web.GetFileByServerRelativeUrlAsync(list.DefaultViewUrl, f => f.PageRenderType);
                        listToAdd.DefaultViewRenderType = defaultViewPage.PageRenderType.ToString();
                    }
                    catch (SharePointRestServiceException ex)
                    {
                        var error = ex.Error as SharePointRestError;

                        // If the exception indicated a non existing file/folder then ignore, else throw
                        if (!ScannerBase.ErrorIndicatesFileFolderDoesNotExists(error))
                        {
                            throw;
                        }
                    }
                }
                else
                {
                    listToAdd.DefaultViewRenderType = PnP.Core.Model.SharePoint.ListPageRenderType.ListTypeNoSupportForModernMode.ToString();
                }
                
                if (listToAdd.AddToDatabase())
                {
                    classicLists.Add(listToAdd);
                }
                else
                {
                    scannerBase.Logger.Information("The list {ListUrl} renders in modern", listToAdd.ListUrl);
                }
            }

            if (classicLists.Count > 0)
            {
                await scannerBase.StorageManager.StoreClassicListInformationAsync(scannerBase.ScanId, classicLists);
            }

        }

        private static bool IsClassicByDesign(PnP.Core.Model.SharePoint.IList list)
        {
            if (list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.Announcements ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.Links ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.DocumentLibrary ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.PictureLibrary ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.WebPageLibrary ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.Announcements ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.IssueTracking ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.Contacts ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.AssetLibrary || 
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.CustomGrid ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.PublishingPagesLibrary || 
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.PromotedLinks || 
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.XMLForm ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.ContentCenterModelLibrary ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.ContentCenterPrimeLibrary ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.ContentCenterSampleLibrary ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.ContentCenterModelUsage ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.ContentCenterExplanation ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.GenericList)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

    }
}
