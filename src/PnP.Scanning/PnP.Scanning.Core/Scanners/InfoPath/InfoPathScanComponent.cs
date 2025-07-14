﻿using Microsoft.SharePoint.Client;
using PnP.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Core.QueryModel;
using PnP.Core;

namespace PnP.Scanning.Core.Scanners
{
    internal static class InfoPathScanComponent
    {
        private static readonly string FormBaseContentType = "0x010101";
        
        internal static async Task ExecuteAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext)
        {
            List<ClassicInfoPath> infoPathLists = new();
            HashSet<string> remediationCodes = new();

            var lists = ScannerBase.CleanLoadedLists(context);

            foreach (var list in lists)
            {
                if (list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.XMLForm ||
                    (!string.IsNullOrEmpty(list.DocumentTemplate) && list.DocumentTemplate.EndsWith(".xsn", StringComparison.InvariantCultureIgnoreCase)))
                {
                    infoPathLists.Add(new ClassicInfoPath
                    {
                        ScanId = scannerBase.ScanId,
                        SiteUrl = scannerBase.SiteUrl,
                        WebUrl = scannerBase.WebUrl,
                        ListUrl = list.RootFolder.ServerRelativeUrl,
                        ListTitle = list.Title,
                        ListId = list.Id,
                        InfoPathUsage = "FormLibrary",
                        InfoPathTemplate = !string.IsNullOrEmpty(list.DocumentTemplate) ? Path.GetFileName(list.DocumentTemplate) : "",
                        Enabled = true,
                        ItemCount = list.ItemCount,
                        LastItemUserModifiedDate = list.LastItemUserModifiedDate,
                        RemediationCode = RemediationCodes.IF2.ToString(),
                    });

                    remediationCodes.Add(RemediationCodes.IF2.ToString());
                }
                else if (list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.DocumentLibrary ||
                         list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.WebPageLibrary)
                {
                    var formContentTypeFound = list.ContentTypes.AsRequested().Where(c => c.Id.StartsWith(FormBaseContentType, StringComparison.InvariantCultureIgnoreCase)).OrderBy(c => c.Id.Length).FirstOrDefault();
                    if (formContentTypeFound != null)
                    {
                        infoPathLists.Add(new ClassicInfoPath
                        {
                            ScanId = scannerBase.ScanId,
                            SiteUrl = scannerBase.SiteUrl,
                            WebUrl = scannerBase.WebUrl,
                            ListUrl = list.RootFolder.ServerRelativeUrl,
                            ListTitle = list.Title,
                            ListId = list.Id,
                            InfoPathUsage = "ContentType",
                            InfoPathTemplate = !string.IsNullOrEmpty(formContentTypeFound.DocumentTemplateUrl) ? Path.GetFileName(formContentTypeFound.DocumentTemplateUrl) : "",
                            Enabled = true,
                            ItemCount = list.ItemCount,
                            LastItemUserModifiedDate = list.LastItemUserModifiedDate,
                            RemediationCode = RemediationCodes.IF2.ToString(),
                        });

                        remediationCodes.Add(RemediationCodes.IF2.ToString());
                    }                    
                }
                else if (list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.GenericList)
                {
                    foreach (var contentType in list.ContentTypes)
                    {
                        try
                        {
                            // Construct folder URL for the content type
                            var folderUrl = $"{list.RootFolder.ServerRelativeUrl}/{contentType.Name}";
                            var folder = await context.Web.GetFolderByServerRelativeUrlAsync(folderUrl, f => f.Properties);
                    
                            if (folder.Properties.Requested &&
                                folder.Properties.GetString("_ipfs_infopathenabled", string.Empty) != string.Empty &&
                                folder.Properties.GetString("_ipfs_solutionName", string.Empty) != string.Empty)
                            {
                                bool infoPathEnabled = true;
                                if (bool.TryParse(folder.Properties.GetString("_ipfs_infopathenabled", string.Empty), out bool infoPathEnabledParsed))
                                {
                                    infoPathEnabled = infoPathEnabledParsed;
                                }
                    
                                infoPathLists.Add(new ClassicInfoPath
                                {
                                    ScanId = scannerBase.ScanId,
                                    SiteUrl = scannerBase.SiteUrl,
                                    WebUrl = scannerBase.WebUrl,
                                    ListUrl = list.RootFolder.ServerRelativeUrl,
                                    ListTitle = list.Title,
                                    ListId = list.Id,
                                    InfoPathUsage = $"CustomForm ({contentType.Name})",
                                    InfoPathTemplate = folder.Properties.GetString("_ipfs_solutionName", string.Empty),
                                    Enabled = infoPathEnabled,
                                    ItemCount = list.ItemCount,
                                    LastItemUserModifiedDate = list.LastItemUserModifiedDate,
                                    RemediationCode = RemediationCodes.IF1.ToString(),
                                });
                    
                                remediationCodes.Add(RemediationCodes.IF1.ToString());
                            }
                        }
                        catch (SharePointRestServiceException ex)
                        {
                            var error = ex.Error as SharePointRestError;
                    
                            // If the exception indicated a non-existing file/folder then ignore, else throw
                            if (!ScannerBase.ErrorIndicatesFileFolderDoesNotExists(error))
                            {
                                throw;
                            }
                        }
                    }
                }
            }

            if (infoPathLists.Count > 0)
            {
                await scannerBase.StorageManager.StoreInfoPathInformationAsync(scannerBase.ScanId, infoPathLists);
            }

            await scannerBase.StorageManager.StoreInfoPathSummaryAsync(scannerBase.ScanId, scannerBase.SiteUrl, scannerBase.WebUrl, scannerBase.WebTemplate, context, remediationCodes, infoPathLists.Count);

        }

    }
}
