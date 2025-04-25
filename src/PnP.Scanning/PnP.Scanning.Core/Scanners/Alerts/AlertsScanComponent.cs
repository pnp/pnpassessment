using Microsoft.SharePoint.Client;
using PnP.Core.Admin.Model.SharePoint;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners
{
    internal static class AlertsScanComponent
    {

        internal static async Task ExecuteAsync(ScannerBase scannerBase, PnPContext context, ClientContext csomContext, VanityUrlOptions vanityUrlOptions)
        {
            List<Alerts> alertsLists = new();

            foreach (var alert in context.Web.Alerts.AsRequested())
            {
                // Drop alerts that are not email alerts (e.g. the alerts created when adding a rule to get notified)
                if (alert.DeliveryChannels != AlertDeliveryMethod.Email)
                {
                    continue;
                }

                // Drop alerts that were created when a user flipped on the "Email Notification" setting in a classic task list
                if (alert.AlertTemplateName.Equals("SPAlertTemplateType.AssignedToNotification", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var newAlert = new Alerts
                {
                    ScanId = scannerBase.ScanId,
                    SiteUrl = scannerBase.SiteUrl,
                    WebUrl = scannerBase.WebUrl,
                    AlertId = alert.Id,
                    AlertTitle = alert.Title,
                    AlertType = alert.AlertType.ToString(),
                    Status = alert.Status.ToString(),
                    DeliveryChannel = alert.DeliveryChannels.ToString(),
                    EventType = alert.EventType.ToString(),
                    AlertFrequency = alert.AlertFrequency.ToString(),
                    CAMLQuery = alert.Filter,
                    ListUrl = alert.ListUrl,
                    ListId = alert.ListId,
                    AlertTemplateName = alert.AlertTemplateName,
                    AlertTime = DateTime.MinValue,
                    ListItemId = 0
                };

                if (alert.IsPropertyAvailable(p => p.List))
                {
                    newAlert.ListTitle = alert.List.IsPropertyAvailable(p => p.Title) ? alert.List.Title : string.Empty;
                }

                if (alert.IsPropertyAvailable(p => p.User)) 
                {
                    newAlert.UserLoginName = alert.User.IsPropertyAvailable(p => p.LoginName) ? alert.User.LoginName : string.Empty;
                    newAlert.UserName = alert.User.IsPropertyAvailable(p => p.Title) ? alert.User.Title : string.Empty;
                    newAlert.UserPrincipalType = alert.User.IsPropertyAvailable(p => p.PrincipalType) ? alert.User.PrincipalType.ToString() : string.Empty;
                    newAlert.UserEmail = alert.User.IsPropertyAvailable(p => p.Mail) ? alert.User.Mail : string.Empty;
                }

                if (alert.AllProperties.Values.TryGetValue("filterindex", out object value))
                {
                    var filterIndex = value.ToString();
                    if (!string.IsNullOrEmpty(filterIndex))
                    {
                        if (int.TryParse(filterIndex, out int filterIndexInt))
                        {
                            newAlert.Filter = filterIndexInt switch
                            {
                                0 => "Anything changes",
                                1 => "Someone else changes an item",
                                2 => "Someone else changes an item created by me",
                                3 => "Someone else changes an item last modified by me",
                                _ => "Custom filtering",
                            };
                        }
                    }
                }

                alertsLists.Add(newAlert);
            }

            if (alertsLists.Count > 0)
            {
                await scannerBase.StorageManager.StoreAlertsInformationAsync(scannerBase.ScanId, alertsLists);
            }
        }
    }
}
