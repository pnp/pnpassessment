﻿using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Storage;
using Serilog;

namespace PnP.Scanning.Core.Services
{
    internal sealed class TelemetryManager
    {
        private bool didInitializeTelemetryData;
        private bool skipTelemetry;
        private bool skipTelemetryEventSent;
        private readonly TelemetryClient telemetryClient;
        private readonly TelemetryConfiguration telemetryConfiguration = TelemetryConfiguration.CreateDefault();

        private const string EngineVersion = "Version";
        private const string AADTenantId = "AADTenantId";
        private const string ScanMode = "Mode";
        private const string Tenant = "Tenant";
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
                telemetryConfiguration.InstrumentationKey = "e9f3c71a-c861-44b1-8e35-b513e101f743";
#else
                telemetryConfiguration.InstrumentationKey = "2563091f-0bef-4ac7-8bb7-532268d52601";
#endif
                telemetryClient = new TelemetryClient(telemetryConfiguration);

                telemetryClient.Context.Session.Id = Guid.NewGuid().ToString();
                telemetryClient.Context.Cloud.RoleInstance = "PnPMicrosoft365Scanner";
                telemetryClient.Context.Device.OperatingSystem = Environment.OSVersion.ToString();
            }
            catch(Exception ex)
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
            catch(Exception ex)
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

                // Populate generic metrics
                await PopulateMetricsAsync(scanId, metrics);

                // Populate event specific metrics
                if (Scan.CLIMode.Equals(Mode.Syntex.ToString()))
                {
                    await PopulateSyntexMetricsAsync(scanId, metrics);
                }
                else if(Scan.CLIMode.Equals(Mode.Workflow.ToString()))
                {
                    await PopulateWorkflowMetricsAsync(scanId, metrics);
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
                var webCount = await dbContext.SiteCollections.CountAsync();
                var failedWebCount = await dbContext.SiteCollections.CountAsync(p => p.Status == SiteWebStatus.Failed);
                var failedSiteCollectionCount = await dbContext.SiteCollections.CountAsync(p => p.Status == SiteWebStatus.Failed);

                int scanDurationInMinutes = 0;
                if (Scan.EndDate != DateTime.MinValue && Scan.EndDate != DateTime.MaxValue && Scan.StartDate != DateTime.MinValue && Scan.StartDate != DateTime.MaxValue)
                {
                    scanDurationInMinutes = (int)(Scan.EndDate - Scan.StartDate).TotalMinutes;
                }

                metric.Add("SiteCollectionCount", siteCollectionCount);
                metric.Add("WebCount", webCount);
                metric.Add("FailedSiteCollectionCount", failedSiteCollectionCount);
                metric.Add("FailedWebCount", failedWebCount);
                metric.Add("ScanDurationInMinutes", scanDurationInMinutes);
            }
        }

        private async Task PopulateWorkflowMetricsAsync(Guid scanId, Dictionary<string, double> metric)
        {
            using (var dbContext = await StorageManager.GetScanContextForDataExportAsync(scanId))
            {
                int count = 0;
                List<Guid> uniqueDefinitions = new();
                List<Guid> uniqueSubscriptions = new();
                int instanceCount = 0;
                int actionCount = 0;
                int unsupportedActionCount = 0;
                int siteScopedCount = 0;
                int listScopedCount = 0;
                int contentTypeScopedCount = 0;
                int considerUpgradingToFlowCount = 0;

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
                int listsWith100To499files = 0;
                int listsWith500to4999files = 0;
                int listsWith5000to49999files = 0;
                int listsWith50000PlusFiles = 0;
                int listsWithLessThen5Fields = 0;
                int listssWith5To9Fields = 0;
                int listsWith10to14Fields = 0;
                int listsWith15PlusFields = 0;

                // Syntex lists metrics
                foreach (var syntexList in dbContext.SyntexLists)
                {
                    listCount++;
                    listFileCount += syntexList.ItemCount;

                    if (syntexList.ItemCount < 100)
                    {
                        listsWithLessThen100Files++;
                    }
                    else if (syntexList.ItemCount < 500)
                    {
                        listsWith100To499files++;
                    }
                    else if (syntexList.ItemCount < 5000)
                    {
                        listsWith500to4999files++;
                    }
                    else if (syntexList.ItemCount < 50000)
                    {
                        listsWith5000to49999files++;
                    }
                    else
                    {
                        listsWith50000PlusFiles++;
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
                }

                metric.Add("ListCount", listCount);
                metric.Add("ListFileCount", listFileCount);
                metric.Add("listsWithLessThan100Files", listsWithLessThen100Files);
                metric.Add("ListsWith100To499files", listsWith100To499files);
                metric.Add("ListsWith500to4999files", listsWith500to4999files);
                metric.Add("ListsWith5000to49999files", listsWith5000to49999files);
                metric.Add("ListsWith50000PlusFiles", listsWith50000PlusFiles);
                metric.Add("ListsWithLessThen5Fields", listsWithLessThen5Fields);
                metric.Add("ListssWith5To9Fields", listssWith5To9Fields);
                metric.Add("ListsWith10to14Fields", listsWith10to14Fields);
                metric.Add("ListsWith15PlusFields", listsWith15PlusFields);
                metric.Add("ListsWithContentTypeCount", listsWithContentTypeCount);
                metric.Add("ListsWithWorkflowCount", listsWithWorkflowCount);
                metric.Add("ListsWithRetentionLabelCount", listsWithRetentionLabelCount);
                metric.Add("ListsWithFlowCount", listsWithFlowCount);

                // Syntex content type metrics
                int contentTypeCount = 0;
                int contentTypesWithLessThen5Fields = 0;
                int contentTypesWith5To9Fields = 0;
                int contentTypesWith10to14Fields = 0;
                int contentTypesWith15PlusFields = 0;
                int contentTypesWithLessThan100Files = 0;
                int contentTypesWith100To499files = 0;
                int contentTypesWith500to4999files = 0;
                int contentTypesWith5000to49999files = 0;
                int contentTypesWith50000PlusFiles = 0;

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

                    if (syntexContentType.FileCount < 100)
                    {
                        contentTypesWithLessThan100Files++;
                    }
                    else if (syntexContentType.FileCount < 500)
                    {
                        contentTypesWith100To499files++;
                    }
                    else if (syntexContentType.FileCount < 5000)
                    {
                        contentTypesWith500to4999files++;
                    }
                    else if (syntexContentType.FileCount < 5000)
                    {
                        contentTypesWith5000to49999files++;
                    }
                    else
                    {
                        contentTypesWith50000PlusFiles++;
                    }
                }

                metric.Add("ContentTypeCount", contentTypeCount);
                metric.Add("ContentTypesWithLessThen5Fields", contentTypesWithLessThen5Fields);
                metric.Add("ContentTypesWith5To9Fields", contentTypesWith5To9Fields);
                metric.Add("ContentTypesWith10to14Fields", contentTypesWith10to14Fields);
                metric.Add("ContentTypesWith15PlusFields", contentTypesWith15PlusFields);
                metric.Add("ContentTypesWithLessThan100Files", contentTypesWithLessThan100Files);
                metric.Add("ContentTypesWith100To499files", contentTypesWith100To499files);
                metric.Add("ContentTypesWith500to4999files", contentTypesWith500to4999files);
                metric.Add("ContentTypesWith5000to49999files", contentTypesWith5000to49999files);
                metric.Add("ContentTypesWith50000PlusFiles", contentTypesWith50000PlusFiles);

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
            properties.Add(Tenant, Scan.CLITenant);
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
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PnPMicrosoft365Scanner-DisableTelemetry")))
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
            if (didInitializeTelemetryData)
            {
                return;
            }
            else
            {
                didInitializeTelemetryData = true;
            }

            if (TenantId == Guid.Empty)
            {
                var scan = await StorageManager.GetTelemetryScanInformationAsync(scanId);
                if (!string.IsNullOrEmpty(scan.CLITenantId) && Guid.TryParse(scan.CLITenantId, out Guid tenantId))
                {
                    Scan = scan;
                    TenantId = tenantId;
                    Log.Information("Tenant id for this scan session is {TenantId}", TenantId);
                }
                else
                {
                    Log.Warning("Tenant id was not retrieved for this scan session");
                }
            }

            if (string.IsNullOrEmpty(Version))
            {
                Version = VersionManager.GetCurrentVersion();
            }            
        }

    }
}
