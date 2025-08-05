﻿using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.EntityFrameworkCore;
using PnP.Core.Admin.Model.SharePoint;
using PnP.Core.Model.SharePoint;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using Serilog;

namespace PnP.Scanning.Core.Services
{
    internal sealed class TelemetryManager
    {
        private bool skipTelemetry;
        private bool skipTelemetryEventSent;
        private readonly TelemetryClient telemetryClient;
        private readonly TelemetryConfiguration telemetryConfiguration = TelemetryConfiguration.CreateDefault();

        private const string EngineVersion = "Version";
        private const string AADTenantId = "AADTenantId";
        private const string ScanMode = "Mode";
        private const string TenantEnvironment = "Environment";
        private const string AuthMode = "AuthMode";
        private const string UsesTenant = "UsesTenant";
        private const string UsesSitesList = "UsesSitesList";
        private const string UsesSitesFile = "UsesSitesFile";
        private const string Threads = "Threads";
        private const string UsesCertPath = "UsesCertPath";
        private const string UsesCertFile = "UsesCertFile";

        public TelemetryManager(StorageManager storageManager)
        {
            StorageManager = storageManager;

            try
            {
#if DEBUG
                telemetryConfiguration.ConnectionString = "InstrumentationKey=e9f3c71a-c861-44b1-8e35-b513e101f743;IngestionEndpoint=https://westus2-2.in.applicationinsights.azure.com/;LiveEndpoint=https://westus2.livediagnostics.monitor.azure.com/;ApplicationId=367148dd-e9be-4dd5-a93c-4343906f39fa";
#else
                telemetryConfiguration.ConnectionString = "InstrumentationKey=2563091f-0bef-4ac7-8bb7-532268d52601;IngestionEndpoint=https://westus2-2.in.applicationinsights.azure.com/;LiveEndpoint=https://westus2.livediagnostics.monitor.azure.com/;ApplicationId=27abf4b2-b1e0-4788-a027-3c77fe94b63b";                
#endif
                telemetryClient = new TelemetryClient(telemetryConfiguration);

                telemetryClient.Context.Session.Id = Guid.NewGuid().ToString();
                telemetryClient.Context.Cloud.RoleInstance = "PnPMicrosoft365Scanner";
                telemetryClient.Context.Device.OperatingSystem = Environment.OSVersion.ToString();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not initialize telemetry client");
                telemetryClient = null;
            }
        }

        internal StorageManager StorageManager { get; private set; }

        internal Storage.Scan Scan { get; private set; }

        internal Guid TenantId { get; set; }

        internal string Version { get; set; }

        internal async Task LogSkipTelemetryAsync(Guid scanId)
        {
            if (telemetryClient == null)
            {
                return;
            }

            await InitializeTelemetryDataAsync(scanId);

            try
            {
                // Prepare event data
                Dictionary<string, string> properties = new();

                // Populate the default properties
                PopulateDefaultProperties(properties);

                // Send the event
                telemetryClient.TrackEvent($"{Scan.CLIMode}{TelemetryEvent.Skip}", properties);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not send telemetry event");
            }
        }

        internal async Task LogEventAsync(Guid scanId, TelemetryEvent telemetryEvent)
        {
            if (telemetryClient == null || skipTelemetry)
            {
                return;
            }

            await InitializeTelemetryAsync(scanId);

            try
            {
                // Prepare event data
                Dictionary<string, string> properties = new()
                {
                    { EngineVersion, Version }
                };

                // Send the event
                telemetryClient.TrackEvent($"{telemetryEvent}", properties);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not send telemetry event");
            }
        }


        internal async Task LogScanEventAsync(Guid scanId, TelemetryEvent telemetryEvent)
        {
            if (telemetryClient == null || skipTelemetry)
            {
                return;
            }

            await InitializeTelemetryAsync(scanId);

            await InitializeTelemetryDataAsync(scanId);

            try
            {
                // Prepare event data
                Dictionary<string, string> properties = new();
                Dictionary<string, double> metrics = new();

                // Populate the default properties
                PopulateDefaultProperties(properties);

                // Populate event properties (if any)
                PopulateScanProperties(properties, scanId);

                // Send the event
                telemetryClient.TrackEvent($"{Scan.CLIMode}{telemetryEvent}", properties, metrics);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not send telemetry event");
            }
        }

        internal async Task LogScanEndAsync(Guid scanId)
        {
            if (telemetryClient == null || skipTelemetry)
            {
                return;
            }

            await InitializeTelemetryAsync(scanId);

            await InitializeTelemetryDataAsync(scanId);

            try
            {
                // Prepare event data
                Dictionary<string, string> properties = new();
                Dictionary<string, double> metrics = new();

                // Populate the default properties
                PopulateDefaultProperties(properties);

                // Populate event properties (if any)
                PopulateScanProperties(properties, scanId);

                // Populate generic scan properties (if any)
                await PopulatePropertiesAsync(scanId, properties);

                // Populate generic metrics
                await PopulateMetricsAsync(scanId, metrics);

                // PER SCAN COMPONENT: Update telemetry data per scan component

                // Populate event specific metrics
                //if (Scan.CLIMode.Equals(Mode.Syntex.ToString()))
                //{
                //    await PopulateSyntexMetricsAsync(scanId, metrics);
                //}
                //else 
                if (Scan.CLIMode.Equals(Mode.Workflow.ToString()))
                {
                    await PopulateWorkflowMetricsAsync(scanId, metrics);
                }
                else if (Scan.CLIMode.Equals(Mode.InfoPath.ToString()))
                {
                    await PopulateInfoPathMetricsAsync(scanId, metrics);
                }
                else if (Scan.CLIMode.Equals(Mode.AddInsACS.ToString()))
                {
                    await PopulateAddInsACSMetricsAsync(scanId, metrics);
                }
                else if (Scan.CLIMode.Equals(Mode.Alerts.ToString()))
                {
                    await PopulateAlertsMetricsAsync(scanId, metrics);
                }

                // Send the event
                telemetryClient.TrackEvent($"{Scan.CLIMode}{TelemetryEvent.Done}", properties, metrics);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not send telemetry event");
            }
            finally
            {
                // Ensure scan done event is sent immediately 
                telemetryClient.Flush();
            }
        }

        private async Task PopulateMetricsAsync(Guid scanId, Dictionary<string, double> metric)
        {
            using (var dbContext = await StorageManager.GetScanContextForDataExportAsync(scanId))
            {
                var siteCollectionCount = await dbContext.SiteCollections.CountAsync();
                var webCount = await dbContext.Webs.CountAsync();
                var failedWebCount = await dbContext.Webs.CountAsync(p => p.Status == SiteWebStatus.Failed);
                var failedSiteCollectionCount = await dbContext.SiteCollections.CountAsync(p => p.Status == SiteWebStatus.Failed);

                int scanDurationInMinutes = 0;
                // Use current value as otherwise end date is not yet set
                var scan = await StorageManager.GetTelemetryScanInformationAsync(scanId);
                if (scan.EndDate != DateTime.MinValue && scan.EndDate != DateTime.MaxValue && scan.StartDate != DateTime.MinValue && scan.StartDate != DateTime.MaxValue)
                {
                    scanDurationInMinutes = (int)(scan.EndDate - scan.StartDate).TotalMinutes;
                }

                metric.Add("SiteCollectionCount", siteCollectionCount);
                metric.Add("WebCount", webCount);
                metric.Add("FailedSiteCollectionCount", failedSiteCollectionCount);
                metric.Add("FailedWebCount", failedWebCount);
                metric.Add("ScanDurationInMinutes", scanDurationInMinutes);
            }
        }

        private async Task PopulateAddInsACSMetricsAsync(Guid scanId, Dictionary<string, double> metric)
        {
            using (var dbContext = await StorageManager.GetScanContextForDataExportAsync(scanId))
            {
                int addInCount = 0;
                int sharePointHostedAddins = 0;
                int providerHostedAddins = 0;

                int sourceCorporateCatalog = 0;
                int sourceMarketplace = 0;
                int sourceDeveloperSite = 0;
                int sourceObjectModel = 0;
                int sourceRemoteObjectModel = 0;
                int sourceSiteCollectionCorporateCatalog = 0;
                int sourceInvalidSource = 0;

                int statusInstalled = 0;
                int statusInstalling = 0;
                int statusUninstalling = 0;
                int statusUpgrading = 0;
                int statusRecycling = 0;
                int statusInvalidStatus = 0;
                int statusCanceling = 0;
                int statusInitialized = 0;
                int statusUpgradeCanceling = 0;
                int statusDisabling = 0;
                int statusDisabled = 0;
                int statusSecretRolling = 0;
                int statusRestoring = 0;
                int statusRestoreCanceling = 0;

                int expiredTrue = 0;
                int expiredFalse = 0;

                foreach (var addIn in dbContext.ClassicAddIns)
                {
                    addInCount++;

                    // Add-In type
                    if (addIn.Type.Equals("SharePoint hosted", StringComparison.InvariantCultureIgnoreCase))
                    {
                        sharePointHostedAddins++;
                    }
                    else if (addIn.Type.Equals("Provider hosted", StringComparison.InvariantCultureIgnoreCase))
                    {
                        providerHostedAddins++;
                    }
                    else if (addIn.Type.Equals("Hybrid", StringComparison.InvariantCultureIgnoreCase))
                    {
                        sharePointHostedAddins++;
                        providerHostedAddins++;
                    }

                    //Add-In source
                    if (addIn.AppSource == SharePointAddInSource.CorporateCatalog.ToString())
                    {
                        sourceCorporateCatalog++;
                    }
                    else if (addIn.AppSource == SharePointAddInSource.Marketplace.ToString())
                    {
                        sourceMarketplace++;
                    }
                    else if (addIn.AppSource == SharePointAddInSource.DeveloperSite.ToString())
                    {
                        sourceDeveloperSite++;
                    }
                    else if (addIn.AppSource == SharePointAddInSource.ObjectModel.ToString())
                    {
                        sourceObjectModel++;
                    }
                    else if (addIn.AppSource == SharePointAddInSource.RemoteObjectModel.ToString())
                    {
                        sourceRemoteObjectModel++;
                    }
                    else if (addIn.AppSource == SharePointAddInSource.SiteCollectionCorporateCatalog.ToString())
                    {
                        sourceSiteCollectionCorporateCatalog++;
                    }
                    else if (addIn.AppSource == SharePointAddInSource.InvalidSource.ToString())
                    {
                        sourceInvalidSource++;
                    }

                    //Add-In status
                    if (addIn.Status == SharePointAddInStatus.Installed.ToString())
                    {
                        statusInstalled++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.Installing.ToString())
                    {
                        statusInstalling++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.Uninstalling.ToString())
                    {
                        statusUninstalling++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.Upgrading.ToString())
                    {
                        statusUpgrading++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.Recycling.ToString())
                    {
                        statusRecycling++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.InvalidStatus.ToString())
                    {
                        statusInvalidStatus++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.Canceling.ToString())
                    {
                        statusCanceling++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.Initialized.ToString())
                    {
                        statusInitialized++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.UpgradeCanceling.ToString())
                    {
                        statusUpgradeCanceling++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.Disabling.ToString())
                    {
                        statusDisabling++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.Disabled.ToString())
                    {
                        statusDisabled++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.SecretRolling.ToString())
                    {
                        statusSecretRolling++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.Restoring.ToString())
                    {
                        statusRestoring++;
                    }
                    else if (addIn.Status == SharePointAddInStatus.RestoreCanceling.ToString())
                    {
                        statusRestoreCanceling++;
                    }

                    if (addIn.HasExpired)
                    {
                        expiredTrue++;
                    }
                    else
                    {
                        expiredFalse++;
                    }

                }

                metric.Add("AddInACSAddInCount", addInCount);
                metric.Add("AddInACSSharePointHostedAddInsCount", sharePointHostedAddins);
                metric.Add("AddInACSProviderHostedAddInsCount", providerHostedAddins);

                metric.Add("AddInACSSourceCorporateCatalogCount", sourceCorporateCatalog);
                metric.Add("AddInACSSourceMarketplaceCount", sourceMarketplace);
                metric.Add("AddInACSSourceDeveloperSiteCount", sourceDeveloperSite);
                metric.Add("AddInACSSourceObjectModelCount", sourceObjectModel);
                metric.Add("AddInACSSourceRemoteObjectModelCount", sourceRemoteObjectModel);
                metric.Add("AddInACSSourceSiteCollectionCorporateCatalogCount", sourceSiteCollectionCorporateCatalog);
                metric.Add("AddInACSSourceInvalidSourceCount", sourceInvalidSource);

                metric.Add("AddInACSStatusInstalledCount", statusInstalled);
                metric.Add("AddInACSStatusInstallingCount", statusInstalling);
                metric.Add("AddInACSStatusUninstallingCount", statusUninstalling);
                metric.Add("AddInACSStatusUpgradingCount", statusUpgrading);
                metric.Add("AddInACSStatusRecyclingCount", statusRecycling);
                metric.Add("AddInACSStatusInvalidStatusCount", statusInvalidStatus);
                metric.Add("AddInACSStatusCancelingCount", statusCanceling);
                metric.Add("AddInACSStatusInitializedCount", statusInitialized);
                metric.Add("AddInACSStatusUpgradeCancelingCount", statusUpgradeCanceling);
                metric.Add("AddInACSStatusDisablingCount", statusDisabling);
                metric.Add("AddInACSStatusDisabledCount", statusDisabled);
                metric.Add("AddInACSStatusSecretRollingCount", statusSecretRolling);
                metric.Add("AddInACSStatusRestoringCount", statusRestoring);
                metric.Add("AddInACSStatusRestoreCancelingCount", statusRestoreCanceling);

                metric.Add("AddInACSAddInExpiredTrueCount", expiredTrue);
                metric.Add("AddInACSAddInExpiredFalseCount", expiredFalse);


                int principalCount = 0;
                int canUseAppOnly = 0;
                int hasExpired = 0;
                int hasTenantPermissions = 0;
                int hasSitePermissions = 0;
                Dictionary<string, int> permissionScopeCounts = new();

                foreach (var acsPrincipal in dbContext.ClassicACSPrincipals)
                {
                    principalCount++;

                    if (acsPrincipal.AllowAppOnly)
                    {
                        canUseAppOnly++;
                    }

                    if (acsPrincipal.HasExpired)
                    {
                        hasExpired++;
                    }

                    if (acsPrincipal.HasTenantScopedPermissions)
                    {
                        hasTenantPermissions++;
                    }

                    if (acsPrincipal.HasSiteCollectionScopedPermissions)
                    {
                        hasSitePermissions++;
                    }

                    foreach (var acsPrincipalPermission in dbContext.ClassicACSPrincipalSiteScopedPermissions.Where(p => p.ScanId == acsPrincipal.ScanId &&
                                                                                                                        p.AppIdentifier == acsPrincipal.AppIdentifier))
                    {
                        string permissionScopeString = "";

                        if (acsPrincipalPermission.ListId != Guid.Empty)
                        {
                            permissionScopeString = $"content/list{acsPrincipalPermission.Right}";
                        }
                        else if (acsPrincipalPermission.WebId != Guid.Empty)
                        {
                            permissionScopeString = $"content/web{acsPrincipalPermission.Right}";
                        }
                        else
                        {
                            permissionScopeString = $"content/site{acsPrincipalPermission.Right}";
                        }

                        if (permissionScopeCounts.ContainsKey(permissionScopeString))
                        {
                            permissionScopeCounts[permissionScopeString]++;
                        }
                        else
                        {
                            permissionScopeCounts.Add(permissionScopeString, 1);
                        }
                    }

                    foreach (var acsPrincipalPermission in dbContext.ClassicACSPrincipalTenantScopedPermissions.Where(p => p.ScanId == acsPrincipal.ScanId &&
                                                                                                                           p.AppIdentifier == acsPrincipal.AppIdentifier))
                    {
                        string permissionScopeString = $"{acsPrincipalPermission.Scope}{acsPrincipalPermission.Right}";

                        if (permissionScopeCounts.ContainsKey(permissionScopeString))
                        {
                            permissionScopeCounts[permissionScopeString]++;
                        }
                        else
                        {
                            permissionScopeCounts.Add(permissionScopeString, 1);
                        }
                    }
                }

                metric.Add("AddInACSPrincipalCount", principalCount);
                metric.Add("AddInACSCanUseAppOnlyCount", canUseAppOnly);
                metric.Add("AddInACSPrincipalHasExpiredCount", hasExpired);
                metric.Add("AddInACSPrincipalHasTenantPermissionsCount", hasTenantPermissions);
                metric.Add("AddInACSPrincipalHasSitePermissionsCount", hasSitePermissions);

                /* Possible permission scope values:
                     
                AddInACSPrincipalPermissioncontentlistguest
                AddInACSPrincipalPermissioncontentlistread
                AddInACSPrincipalPermissioncontentlistwrite
                AddInACSPrincipalPermissioncontentlistmanage
                AddInACSPrincipalPermissioncontentlistfullcontrol
                AddInACSPrincipalPermissioncontentwebguest
                AddInACSPrincipalPermissioncontentwebread
                AddInACSPrincipalPermissioncontentwebwrite
                AddInACSPrincipalPermissioncontentwebmanage
                AddInACSPrincipalPermissioncontentwebfullcontrol
                AddInACSPrincipalPermissioncontentsiteguest
                AddInACSPrincipalPermissioncontentsiteread
                AddInACSPrincipalPermissioncontentsitewrite
                AddInACSPrincipalPermissioncontentsitemanage
                AddInACSPrincipalPermissioncontentsitefullcontrol
                AddInACSPrincipalPermissioncontenttenantguest
                AddInACSPrincipalPermissioncontenttenantread
                AddInACSPrincipalPermissioncontenttenantwrite
                AddInACSPrincipalPermissioncontenttenantmanage
                AddInACSPrincipalPermissioncontenttenantfullcontrol
                AddInACSPrincipalPermissionsearchqueryasuserignoreappprincipal
                AddInACSPrincipalPermissiontaxonomyread
                AddInACSPrincipalPermissiontaxonomywrite
                AddInACSPrincipalPermissionsocialtenantread
                AddInACSPrincipalPermissionsocialtenantwrite
                AddInACSPrincipalPermissionsocialtenantmanage
                AddInACSPrincipalPermissionsocialtenantfullcontrol
                AddInACSPrincipalPermissionsocialtrimmingread
                AddInACSPrincipalPermissionsocialtrimmingwrite
                AddInACSPrincipalPermissionsocialtrimmingmanage
                AddInACSPrincipalPermissionsocialtrimmingfullcontrol
                AddInACSPrincipalPermissionprojectservermanage
                AddInACSPrincipalPermissionprojectserverprojectsread
                AddInACSPrincipalPermissionprojectserverprojectswrite
                AddInACSPrincipalPermissionprojectserverprojectsprojectread
                AddInACSPrincipalPermissionprojectserverprojectsprojectwrite
                AddInACSPrincipalPermissionprojectserverenterpriseresourcesread
                AddInACSPrincipalPermissionprojectserverenterpriseresourceswrite
                AddInACSPrincipalPermissionprojectserverstatusingsubmitstatus
                AddInACSPrincipalPermissionprojectserverreportingread
                AddInACSPrincipalPermissionprojectserverworkflowelevate

                 */

                foreach (var permissionScope in permissionScopeCounts)
                {
                    metric.Add($"AddInACSPrincipalPermission{permissionScope.Key.Replace("/", "" ).ToLowerInvariant()}", permissionScope.Value);
                }

            }
        }

        private async Task PopulateAlertsMetricsAsync(Guid scanId, Dictionary<string, double> metric)
        {
            using (var dbContext = await StorageManager.GetScanContextForDataExportAsync(scanId))
            {
                int alertCount = 0;

                // Alert frequency breakdown
                int immediateAlerts = 0;
                int dailyAlerts = 0;
                int weeklyAlerts = 0;

                // Alert type breakdown
                int listAlerts = 0;
                int listItemAlerts = 0;  

                foreach (var alert in dbContext.Alerts)
                {
                    alertCount++;

                    // Categorize alerts based on their frequency
                    if (Enum.TryParse(alert.AlertFrequency, out AlertFrequency frequency))
                    {
                        switch (frequency)
                        {
                            case AlertFrequency.Immediately:
                                immediateAlerts++;
                                break;
                            case AlertFrequency.Daily:
                                dailyAlerts++;
                                break;
                            case AlertFrequency.Weekly:
                                weeklyAlerts++;
                                break;
                        }
                    }

                    if (Enum.TryParse(alert.AlertType, out AlertType type))
                    {
                        switch (type)
                        {
                            case AlertType.List:
                                listAlerts++;
                                break;
                            case AlertType.ListItem:
                                listItemAlerts++;
                                break;
                        }
                    }
                }

                // Add metrics to the dictionary
                metric.Add("AlertsCount", alertCount);

                // Add frequency breakdown to the dictionary
                metric.Add("AlertsImmediate", immediateAlerts);
                metric.Add("AlertsDaily", dailyAlerts);
                metric.Add("AlertsWeekly", weeklyAlerts);

                // Add type breakdown to the dictionary
                metric.Add("AlertsList", listAlerts);
                metric.Add("AlertsListItem", listItemAlerts);
            }
        }

        private async Task PopulateInfoPathMetricsAsync(Guid scanId, Dictionary<string, double> metric)
        {
            using (var dbContext = await StorageManager.GetScanContextForDataExportAsync(scanId))
            {
                int count = 0;
                int formLibraryCount = 0;
                int contentTypeCount = 0;
                int customFormCount = 0;

                foreach (var infoPath in dbContext.ClassicInfoPath)
                {
                    count++;
                    if (infoPath.InfoPathUsage.Equals("FormLibrary", StringComparison.InvariantCultureIgnoreCase))
                    {
                        formLibraryCount++;
                    }
                    else if (infoPath.InfoPathUsage.Equals("ContentType", StringComparison.InvariantCultureIgnoreCase))
                    {
                        contentTypeCount++;
                    }
                    else if (infoPath.InfoPathUsage.Equals("CustomForm", StringComparison.InvariantCultureIgnoreCase))
                    {
                        customFormCount++;
                    }                    
                }

                metric.Add("InfoPathCount", count);
                metric.Add("InfoPathFormLibraryCount", formLibraryCount);
                metric.Add("InfoPathContentTypeCount", contentTypeCount);
                metric.Add("InfoPathCustomFormCount", customFormCount);
            }
        }

        private async Task PopulateWorkflowMetricsAsync(Guid scanId, Dictionary<string, double> metric)
        {
            using (var dbContext = await StorageManager.GetScanContextForDataExportAsync(scanId))
            {
                int count = 0;
                List<Guid> uniqueDefinitions = new();
                List<Guid> uniqueSubscriptions = new();
                Dictionary<string, int> actionCounts = new();
                int instanceCount = 0;
                int actionCount = 0;
                int unsupportedActionCount = 0;
                int siteScopedCount = 0;
                int listScopedCount = 0;
                int contentTypeScopedCount = 0;
                int considerUpgradingToFlowCount = 0;

                // Add a line for each possible action
                foreach(var defaultWorkflow in WorkflowManager.Instance.DefaultActions)
                {
                    actionCounts.Add(defaultWorkflow.ActionNameShort, 0);
                }

                foreach(var workflow in dbContext.Workflows)
                {
                    count++;
                    instanceCount += workflow.TotalInstances;
                    actionCount += workflow.ActionCount;
                    unsupportedActionCount += workflow.UnsupportedActionCount;
                    
                    if(workflow.Scope.Equals("Site", StringComparison.OrdinalIgnoreCase))
                    {
                        siteScopedCount++;
                    }
                    else if (!string.IsNullOrEmpty(workflow.ContentTypeId))
                    {
                        contentTypeScopedCount++;
                    }
                    else
                    {
                        listScopedCount++;
                    }

                    if (!uniqueDefinitions.Contains(workflow.DefinitionId))
                    {
                        uniqueDefinitions.Add(workflow.DefinitionId);
                    }

                    if (workflow.SubscriptionId != Guid.Empty && !uniqueSubscriptions.Contains(workflow.SubscriptionId))
                    {
                        uniqueSubscriptions.Add(workflow.SubscriptionId);
                    }

                    if (workflow.ConsiderUpgradingToFlow)
                    {
                        considerUpgradingToFlowCount++;
                    }

                    if (!string.IsNullOrEmpty(workflow.UsedActions))
                    {
                        var actionList = workflow.UsedActions.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach(var action in actionList)
                        {
                            if (actionCounts.ContainsKey(action))
                            {
                                actionCounts[action]++;
                            }
                        }
                    }
                }

                metric.Add("WorkflowCount", count);
                metric.Add("WorkflowDefinitionCount", uniqueDefinitions.Count);
                metric.Add("WorkflowSubscriptionCount", uniqueSubscriptions.Count);
                metric.Add("InstancesInLast30DaysCount", instanceCount);
                metric.Add("ConsiderUpgradingToFlowCount", considerUpgradingToFlowCount);
                metric.Add("ToFlowMappingPercentage", (int)((actionCount - unsupportedActionCount) / (double)actionCount * 100));
                metric.Add("SiteScopedCount", siteScopedCount);
                metric.Add("ListScopedCount", listScopedCount);
                metric.Add("ContentTypeScopedCount", contentTypeScopedCount);

                foreach(var workflowAction in actionCounts)
                {
                    metric.Add($"Activity{workflowAction.Key}", workflowAction.Value);
                }
            }
        }

        private async Task PopulatePropertiesAsync(Guid scanId, Dictionary<string, string> properties)
        {
            using (var dbContext = await StorageManager.GetScanContextForDataExportAsync(scanId))
            {
                // Scan properties
                foreach (var scanProperty in dbContext.Properties)
                {
                    // Telemetry processing picks up "syntexdeepscan", so ensure we always send this property using that name
                    if (scanProperty.Name.Equals(Constants.StartSyntexFull, StringComparison.OrdinalIgnoreCase))
                    {
                        properties.Add("syntexdeepscan", scanProperty.Value);
                    }
                    else
                    {
                        properties.Add(scanProperty.Name, scanProperty.Value);
                    }
                }
            }
        }

        private async Task PopulateSyntexMetricsAsync(Guid scanId, Dictionary<string, double> metric)
        {
            using (var dbContext = await StorageManager.GetScanContextForDataExportAsync(scanId))
            {
                int listCount = 0;
                int listsWithContentTypeCount = 0;
                long listFileCount = 0;
                int listsWithWorkflowCount = 0;
                int listsWithRetentionLabelCount = 0;
                int listsWithFlowCount = 0;
                int listsWithLessThen100Files = 0;
                int listsWith100To499Files = 0;
                int listsWith500to4999Files = 0;
                int listsWith5000to49999Files = 0;
                int listsWith50000PlusFiles = 0;
                int listsWithLessThen50Folders = 0;
                int listsWith50To259Folders = 0;
                int listsWith250to2499Folders = 0;
                int listsWith2500to24999Folders = 0;
                int listsWith25000PlusFolders = 0;
                int listsWithLessThen5Fields = 0;
                int listssWith5To9Fields = 0;
                int listsWith10to14Fields = 0;
                int listsWith15PlusFields = 0;
                int smallLibraryCount = 0;
                int mediumLibraryCount = 0;
                int largeLibraryCount = 0;

                // Was deep scan used?
                bool deepScanUsed = false;
                var deepScanProperty = await dbContext.Properties.FirstOrDefaultAsync(p => p.Name == Constants.StartSyntexFull);
                if (deepScanProperty != null)
                {
                    if (deepScanProperty.Value.Equals("True", StringComparison.InvariantCultureIgnoreCase))
                    {
                        deepScanUsed = true;
                    }
                    else
                    {
                        deepScanUsed = false;
                    }
                }

                // Syntex lists metrics
                foreach (var syntexList in dbContext.SyntexLists)
                {
                    int valueToUse = syntexList.DocumentCount;

                    if (!deepScanUsed)
                    {
                        valueToUse = syntexList.ItemCount;
                    }

                    listCount++;                    
                    listFileCount += valueToUse;                    

                    if (valueToUse < 100)
                    {
                        listsWithLessThen100Files++;
                    }
                    else if (valueToUse < 500)
                    {
                        listsWith100To499Files++;
                    }
                    else if (valueToUse < 5000)
                    {
                        listsWith500to4999Files++;
                    }
                    else if (valueToUse < 50000)
                    {
                        listsWith5000to49999Files++;
                    }
                    else
                    {
                        listsWith50000PlusFiles++;
                    }

                    if (syntexList.FolderCount < 50)
                    {
                        listsWithLessThen50Folders++;
                    }
                    else if (syntexList.FolderCount < 250)
                    {
                        listsWith50To259Folders++;
                    }
                    else if (syntexList.FolderCount < 2500)
                    {
                        listsWith250to2499Folders++;
                    }
                    else if (syntexList.FolderCount < 25000)
                    {
                        listsWith2500to24999Folders++;
                    }
                    else
                    {
                        listsWith25000PlusFolders++;
                    }

                    if (syntexList.FieldCount < 5)
                    {
                        listsWithLessThen5Fields++;
                    }
                    else if (syntexList.FieldCount < 10)
                    {
                        listssWith5To9Fields++;
                    }
                    else if (syntexList.FieldCount < 15)
                    {
                        listsWith10to14Fields++;
                    }
                    else
                    {
                        listsWith15PlusFields++;
                    }

                    if (syntexList.AllowContentTypes)
                    {
                        listsWithContentTypeCount++;
                    }

                    if (syntexList.WorkflowInstanceCount > 0)
                    {
                        listsWithWorkflowCount++;
                    }

                    if (syntexList.RetentionLabelCount > 0)
                    {
                        listsWithRetentionLabelCount++;
                    }

                    if (syntexList.FlowInstanceCount > 0)
                    {
                        listsWithFlowCount++;
                    }

                    if (syntexList.LibrarySize == "Small")
                    {
                        smallLibraryCount++;
                    }
                    else if (syntexList.LibrarySize == "Medium")
                    {
                        mediumLibraryCount++;
                    }
                    else if (syntexList.LibrarySize == "Large")
                    {
                        largeLibraryCount++;
                    }
                }

                metric.Add("ListCount", listCount);
                metric.Add("ListFileCount", listFileCount);
                metric.Add("listsWithLessThan100Files", listsWithLessThen100Files);
                metric.Add("ListsWith100To499Files", listsWith100To499Files);
                metric.Add("ListsWith500to4999Files", listsWith500to4999Files);
                metric.Add("ListsWith5000to49999Files", listsWith5000to49999Files);
                metric.Add("ListsWith50000PlusFiles", listsWith50000PlusFiles);
                metric.Add("ListsWithLessThen50Folders", listsWithLessThen50Folders);
                metric.Add("ListsWith50To259Folders", listsWith50To259Folders);
                metric.Add("ListsWith250to2499Folders", listsWith250to2499Folders);
                metric.Add("ListsWith2500to24999Folders", listsWith2500to24999Folders);
                metric.Add("ListsWith25000PlusFolders", listsWith25000PlusFolders);
                metric.Add("ListsWithLessThen5Fields", listsWithLessThen5Fields);
                metric.Add("ListssWith5To9Fields", listssWith5To9Fields);
                metric.Add("ListsWith10to14Fields", listsWith10to14Fields);
                metric.Add("ListsWith15PlusFields", listsWith15PlusFields);
                metric.Add("ListsWithContentTypeCount", listsWithContentTypeCount);
                metric.Add("ListsWithWorkflowCount", listsWithWorkflowCount);
                metric.Add("ListsWithRetentionLabelCount", listsWithRetentionLabelCount);
                metric.Add("ListsWithFlowCount", listsWithFlowCount);
                metric.Add("ListsLargeCount", largeLibraryCount);
                metric.Add("ListsMediumCount", mediumLibraryCount);
                metric.Add("ListsSmallCount", smallLibraryCount);

                // Syntex content type metrics
                int contentTypeCount = 0;
                int contentTypesWithLessThen5Fields = 0;
                int contentTypesWith5To9Fields = 0;
                int contentTypesWith10to14Fields = 0;
                int contentTypesWith15PlusFields = 0;
                int contentTypesWithLessThan100Items = 0;
                int contentTypesWith100To499Items = 0;
                int contentTypesWith500to4999Items = 0;
                int contentTypesWith5000to49999Items = 0;
                int contentTypesWith50000PlusItems = 0;

                foreach (var syntexContentType in dbContext.SyntexContentTypes)
                {
                    contentTypeCount++;

                    if (syntexContentType.FieldCount < 5)
                    {
                        contentTypesWithLessThen5Fields++;
                    }
                    else if (syntexContentType.FieldCount < 10)
                    {
                        contentTypesWith5To9Fields++;
                    }
                    else if (syntexContentType.FieldCount < 15)
                    {
                        contentTypesWith10to14Fields++;
                    }
                    else
                    {
                        contentTypesWith15PlusFields++;
                    }

                    if (syntexContentType.ItemCount < 100)
                    {
                        contentTypesWithLessThan100Items++;
                    }
                    else if (syntexContentType.ItemCount < 500)
                    {
                        contentTypesWith100To499Items++;
                    }
                    else if (syntexContentType.ItemCount < 5000)
                    {
                        contentTypesWith500to4999Items++;
                    }
                    else if (syntexContentType.ItemCount < 5000)
                    {
                        contentTypesWith5000to49999Items++;
                    }
                    else
                    {
                        contentTypesWith50000PlusItems++;
                    }
                }

                metric.Add("ContentTypeCount", contentTypeCount);
                metric.Add("ContentTypesWithLessThen5Fields", contentTypesWithLessThen5Fields);
                metric.Add("ContentTypesWith5To9Fields", contentTypesWith5To9Fields);
                metric.Add("ContentTypesWith10to14Fields", contentTypesWith10to14Fields);
                metric.Add("ContentTypesWith15PlusFields", contentTypesWith15PlusFields);
                metric.Add("ContentTypesWithLessThan100Items", contentTypesWithLessThan100Items);
                metric.Add("ContentTypesWith100To499Items", contentTypesWith100To499Items);
                metric.Add("ContentTypesWith500to4999Items", contentTypesWith500to4999Items);
                metric.Add("ContentTypesWith5000to49999Items", contentTypesWith5000to49999Items);
                metric.Add("ContentTypesWith50000PlusItems", contentTypesWith50000PlusItems);

                // Syntex usage metrics
                List<string> uniqueContentCenters = new();
                List<string> uniqueClassifiers = new();
                List<Guid> uniqueTargetLists = new();
                long classifiedFileCount = 0;
 
                foreach (var syntexModelUsage in dbContext.SyntexModelUsage)
                {
                    if (!uniqueContentCenters.Contains($"{syntexModelUsage.SiteUrl}{syntexModelUsage.WebUrl}"))
                    {
                        uniqueContentCenters.Add($"{syntexModelUsage.SiteUrl}{syntexModelUsage.WebUrl}");
                    }

                    if (!uniqueTargetLists.Contains(syntexModelUsage.TargetListId))
                    {
                        uniqueTargetLists.Add(syntexModelUsage.TargetListId);
                    }

                    if (!uniqueClassifiers.Contains($"{syntexModelUsage.SiteUrl}{syntexModelUsage.WebUrl}{syntexModelUsage.Classifier}"))
                    {
                        uniqueClassifiers.Add($"{syntexModelUsage.SiteUrl}{syntexModelUsage.WebUrl}{syntexModelUsage.Classifier}");
                    }

                    classifiedFileCount += syntexModelUsage.ClassifiedItemCount;
                }

                metric.Add("ModelUsageContentCenterCount", uniqueContentCenters.Count);
                metric.Add("ModelUsageTargetedListsCount", uniqueTargetLists.Count);
                metric.Add("ModelUsageClassifierCount", uniqueClassifiers.Count);
                metric.Add("ModelUsageClassifiedFileCount", classifiedFileCount);
            }
        }

        private void PopulateScanProperties(Dictionary<string, string> properties, Guid scanId)
        {
            properties.Add("ScanId", scanId.ToString());
            properties.Add(UsesTenant, (!string.IsNullOrEmpty(Scan.CLITenant)).ToString());
            properties.Add(TenantEnvironment, Scan.CLIEnvironment);
            properties.Add(AuthMode, Scan.CLIAuthMode);
            properties.Add(UsesSitesList, (!string.IsNullOrEmpty(Scan.CLISiteList)).ToString());
            properties.Add(UsesSitesFile, (!string.IsNullOrEmpty(Scan.CLISiteFile)).ToString());
            properties.Add(Threads, Scan.CLIThreads.ToString());
            properties.Add(UsesCertPath, (!string.IsNullOrEmpty(Scan.CLICertPath)).ToString());
            properties.Add(UsesCertFile, (!string.IsNullOrEmpty(Scan.CLICertFile)).ToString());
        }

        private void PopulateDefaultProperties(Dictionary<string, string> properties)
        {
            properties.Add(EngineVersion, Version);
            properties.Add(AADTenantId, TenantId.ToString());
            properties.Add(ScanMode, Scan.CLIMode);
        }

        private async Task InitializeTelemetryAsync(Guid scanId)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PNP_DISABLETELEMETRY")))
            {
                skipTelemetry = true;
            }

            if (skipTelemetry && !skipTelemetryEventSent)
            {
                if (scanId != Guid.Empty)
                {
                    skipTelemetryEventSent = true;
                    await LogSkipTelemetryAsync(scanId);
                }
            }
        }

        private async Task InitializeTelemetryDataAsync(Guid scanId)
        {
            var scan = await StorageManager.GetTelemetryScanInformationAsync(scanId);
            if (!string.IsNullOrEmpty(scan.CLITenantId) && Guid.TryParse(scan.CLITenantId, out Guid tenantId))
            {
                Scan = scan;
                TenantId = tenantId;
                Log.Information("Tenant id for this assessment session {ScanId} is {TenantId}. Assessment mode = {Mode}", scanId, TenantId, Scan.CLIMode);
            }
            else
            {
                Log.Warning("Tenant id was not retrieved for this assessment session");
            }

            if (string.IsNullOrEmpty(Version))
            {
                Version = VersionManager.GetCurrentVersion();
            }            
        }

    }
}
