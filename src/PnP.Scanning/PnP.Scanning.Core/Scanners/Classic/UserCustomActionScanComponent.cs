using Microsoft.SharePoint.Client;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Storage;
using System.Xml;

namespace PnP.Scanning.Core.Scanners
{
    internal static class UserCustomActionScanComponent
    {
        
        internal static async Task ExecuteAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext)
        {
            List<ClassicUserCustomAction> userCustomActionsList = new();
            
            var lists = ScannerBase.CleanLoadedLists(context);

            // Process site user custom actions
            if (scannerBase.WebUrl == "/")
            {
                ProcessUserCustomActions(userCustomActionsList, scannerBase,  context.Site.UserCustomActions);
            }

            // Process web user custom actions
            ProcessUserCustomActions(userCustomActionsList, scannerBase, context.Web.UserCustomActions);

            // Process list user custom actions
            foreach (var list in lists)
            {
                ProcessUserCustomActions(userCustomActionsList, scannerBase, list.UserCustomActions, list);
            }

            if (userCustomActionsList.Count > 0)
            {
                await scannerBase.StorageManager.StoreClassicUserCustomActionInformationAsync(scannerBase.ScanId, userCustomActionsList);
            }
        }

        private static void ProcessUserCustomActions(List<ClassicUserCustomAction> userCustomActionsList, ScannerBase scannerBase, IUserCustomActionCollection userCustomActions, IList list = null)
        {
            foreach(var userCustomAction in userCustomActions.AsRequested())
            {
                var userCustomActionToAdd = new ClassicUserCustomAction
                {
                    Id = userCustomAction.Id,
                    ScanId = scannerBase.ScanId,
                    SiteUrl = scannerBase.SiteUrl,
                    WebUrl = scannerBase.WebUrl,
                    Title = userCustomAction.Title,
                    Name = userCustomAction.Name,
                    Location = userCustomAction.Location,
                    RegistrationType = userCustomAction.RegistrationType.ToString(),
                    RegistrationId = userCustomAction.RegistrationId,
                    CommandUIExtension = userCustomAction.CommandUIExtension,
                    Url = userCustomAction.Url,
                    Description = userCustomAction.Description,
                    ScriptBlock = "",
                    ScriptSrc = ""
                };

                if (!string.IsNullOrEmpty(userCustomAction.Location))
                {
                    if (!(userCustomAction.Location.Equals("EditControlBlock", StringComparison.InvariantCultureIgnoreCase) ||
                          userCustomAction.Location.StartsWith("ClientSideExtension.", StringComparison.InvariantCultureIgnoreCase) ||
                          userCustomAction.Location.Equals("CommandUI.Ribbon", StringComparison.InvariantCultureIgnoreCase)))
                    {
                        userCustomActionToAdd.ScriptBlock = userCustomAction.ScriptBlock ?? "";
                        userCustomActionToAdd.ScriptSrc = userCustomAction.ScriptSrc ?? "";
                        userCustomActionToAdd.Problem = "InvalidLocation";
                    }
                }

                if (!string.IsNullOrEmpty(userCustomAction.CommandUIExtension))
                {
                    XmlDocument doc = new();
                    string xmlString = userCustomAction.CommandUIExtension;
                    xmlString = xmlString.Replace("http://schemas.microsoft.com/sharepoint/", "");
                    doc.LoadXml(xmlString);

                    XmlNodeList handlers = doc.SelectNodes("/CommandUIExtension/CommandUIHandlers/CommandUIHandler");
                    foreach (XmlNode handler in handlers)
                    {
                        if (handler.Attributes["CommandAction"] != null && handler.Attributes["CommandAction"].Value.ToLower().Contains("javascript"))
                        {
                            userCustomActionToAdd.CommandAction = handler.Attributes["CommandAction"].Value;
                            userCustomActionToAdd.Problem = !string.IsNullOrEmpty(userCustomActionToAdd.Problem) ? $"{userCustomActionToAdd.Problem},JavaScriptEmbedded" : "JavaScriptEmbedded";
                            break;
                        }
                    }
                }                

                if (list != null)
                {
                    userCustomActionToAdd.ListUrl = list.RootFolder.ServerRelativeUrl;
                    userCustomActionToAdd.ListTitle = list.Title;
                    userCustomActionToAdd.ListId = list.Id;
                }

                if (!string.IsNullOrEmpty(userCustomActionToAdd.Problem))
                {
                    userCustomActionsList.Add(userCustomActionToAdd);
                }                

            }
        }

    }
}
