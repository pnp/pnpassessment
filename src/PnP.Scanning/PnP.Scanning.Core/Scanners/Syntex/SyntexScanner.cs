using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using System.Globalization;
using System.Linq.Expressions;
using System.Xml;
using MathNet.Numerics.Statistics;
using Microsoft.SharePoint.Client.WorkflowServices;
using Microsoft.SharePoint.Client;

namespace PnP.Scanning.Core.Scanners
{
    internal class SyntexScanner : ScannerBase
    {
        private readonly string UsesApplicationPermissons = "UsesApplicationPermissons";
        private readonly string HasSitesFullControlAll = "HasSitesFullControlAll";
        private readonly string HasPermissionsToReadWorkflowData = "HasPermissionsToReadWorkflowData";

        private class ContentTypeInfo
        {
            internal ContentTypeInfo(string contentTypeId, string schemaXml)
            {
                ContentTypeId = contentTypeId;
                SchemaXml = schemaXml;
            }

            internal string ContentTypeId { get; set; }
            internal string SchemaXml { get; set; }
        }

        private class ContentTypeItemUsage
        {
            internal ContentTypeItemUsage(int count)
            {
                Count = count;
            }

            internal Dictionary<Guid, double> ContentTypePerList { get; set; } = new Dictionary<Guid, double>();

            internal int Count { get; set; }

            internal double Min { get; set; } = 0;

            internal double Max { get; set; } = 0;

            internal double Mean { get; set; } = 0;

            internal double StandardDeviation { get; set; } = 0;

            internal double Median { get; set; } = 0;

            internal double LowerQuartile { get; set; } = 0;

            internal double UpperQuartile { get; set; } = 0;
        }


        public SyntexScanner(ScanManager scanManager, StorageManager storageManager, IPnPContextFactory pnpContextFactory, 
                             CsomEventHub csomEventHub, Guid scanId, string siteUrl, string webUrl, SyntexOptions options) : 
            base(scanManager, storageManager, pnpContextFactory, csomEventHub, scanId, siteUrl, webUrl)
        {
            Options = options;
        }

        internal SyntexOptions Options { get; set; }


        internal async override Task ExecuteAsync()
        {
            Logger.Information("Starting Syntex assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

            // Define extra Web/Site data that we want to load when the context is inialized
            // This will not require extra server roundtrips
            PnPContextOptions options = new()
            {
                AdditionalWebPropertiesOnCreate = new Expression<Func<IWeb, object>>[]
                {
                    w => w.WebTemplateConfiguration,
                    w => w.Lists.QueryProperties(r => r.Title, r => r.ItemCount, r => r.ListExperience, r => r.TemplateType, r => r.ContentTypesEnabled, r => r.Hidden, 
                                                 r => r.Created, r => r.LastItemUserModifiedDate, r => r.IsSiteAssetsLibrary, r => r.IsSystemList,
                                                 r => r.Fields.QueryProperties(f => f.Id, f => f.Hidden, f => f.TypeAsString, f => f.InternalName, f => f.StaticName, f => f.TermSetId, f => f.Title, f => f.Required),
                                                 r => r.ContentTypes.QueryProperties(c => c.Id, c => c.StringId, c=> c.Name, c => c.Hidden, c => c.Group, c => c.SchemaXml,
                                                    c => c.Fields.QueryProperties(f => f.Id, f => f.Hidden, f => f.TypeAsString, f => f.InternalName, f => f.StaticName, f => f.TermSetId, f => f.Title, f => f.Required), 
                                                    c => c.FieldLinks.QueryProperties(f => f.Id, f => f.Hidden, f => f.FieldInternalName, f => f.Required)),
                                                 r => r.RootFolder.QueryProperties(p => p.ServerRelativeUrl))
                }
            };

            using (var context = await GetPnPContextAsync(options))
            {
                //List<IList> syntexListInstances = new();
                List<SyntexList> syntexLists = new();
                List<SyntexContentType> syntexContentTypes = new();
                List<SyntexContentTypeField> syntexContentTypeFields = new();
                List<SyntexField> syntexFields = new();
                List<SyntexModelUsage> syntexModelUsage = new();

                List<ContentTypeInfo> uniqueContentTypesInWeb = new();

                // Loop over the enumerated lists
                foreach (var list in context.Web.Lists.AsRequested())
                {
                    // Only include the lists which make sense to include
                    if (IncludeList(list))
                    {
                        Logger.Information("Processing list {ListUrl} for {SiteUrl}{WebUrl}", list.RootFolder.ServerRelativeUrl, SiteUrl, WebUrl);

                        // Process list information
                        var syntexList = PrepareSyntexList(list);
                        var foundSyntexFields = PrepareSyntexFields(list);

                        syntexList.FieldCount = foundSyntexFields.Count;

                        syntexLists.Add(syntexList);
                        //syntexListInstances.Add(list);

                        if (list.ContentTypesEnabled)
                        {
                            // Process content type information
                            foreach(var contentType in list.ContentTypes.AsRequested())
                            {
                                (SyntexContentType syntexContentType, string schemaXml, List<SyntexContentTypeField> syntexContentTypeFieldsCollection) = PrepareSyntexContentType(list, contentType);
                                if (syntexContentType != null && syntexContentTypeFieldsCollection != null && schemaXml != null)
                                {
                                    syntexContentTypes.Add(syntexContentType);
                                    syntexContentTypeFields.AddRange(syntexContentTypeFieldsCollection.ToArray());

                                    // keep track of a list with unique content type ids
                                    if (uniqueContentTypesInWeb.FirstOrDefault(p => p.ContentTypeId == syntexContentType.ContentTypeId) == null)
                                    {
                                        uniqueContentTypesInWeb.Add(new ContentTypeInfo(syntexContentType.ContentTypeId, schemaXml));
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Process field information
                            syntexFields.AddRange(foundSyntexFields.ToArray());
                        }
                    }
                    else
                    {
                        Logger.Debug("Skipping list {ListUrl} for {SiteUrl}{WebUrl}", list.RootFolder.ServerRelativeUrl, SiteUrl, WebUrl);
                    }
                }

                // Process the unique content type ids
                foreach (var contentType in uniqueContentTypesInWeb)
                {
                    if (!await StorageManager.IsContentTypeStoredAsync(ScanId, contentType.ContentTypeId))
                    {
                        // Get the first occurance
                        var contentTypeInstance = syntexContentTypes.First(p => p.ContentTypeId == contentType.ContentTypeId);

                        // Analyze the SchemaXml to detect if this is a syntex created content type
                        (string driveId, string modelId) = IsSyntexContentType(contentType.SchemaXml);

                        SyntexContentTypeSummary syntexContentTypeSummary = new()
                        {
                            ScanId = contentTypeInstance.ScanId,
                            ContentTypeId = contentTypeInstance.ContentTypeId,
                            FieldCount = contentTypeInstance.FieldCount,
                            Group = contentTypeInstance.Group,
                            Hidden = contentTypeInstance.Hidden,
                            Name = contentTypeInstance.Name,
                            IsSyntexContentType = driveId != null,
                            SyntexModelDriveId = driveId,
                            SyntexModelObjectId = modelId
                        };

                        await StorageManager.AddToContentTypeSummaryAsync(ScanId, syntexContentTypeSummary);
                    }
                }

                // Calculate content type file and label counts
                await CalculateContentTypeCountsAsync(context, syntexContentTypes, syntexLists);

                // Calculate label counts for the remaining lists
                await CalculateCountsForRemainingListsAsync(context, syntexContentTypes, syntexLists);

                // Scan for Workflow 2013 instances on the collected lists
                await ScanForListWorkflowAsync(syntexLists);

                // Scan for PowerAutomate flow instances on the collected lists
                //await ScanForPowerAutomateFlowsAsync(context, syntexListInstances, syntexLists);

                // Persist the Syntex model usage data in case we're processing a Syntex Content Center
                if (context.Web.WebTemplateConfiguration == "CONTENTCTR#0")
                {
                    await CaptureSyntexModelUsageDataAsync(context, syntexModelUsage);
                }

                // Persist the gathered data
                await StorageManager.StoreSyntexInformationAsync(ScanId, syntexLists, syntexContentTypes, syntexContentTypeFields, syntexFields, syntexModelUsage);
            }

            Logger.Information("Syntex assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
        }

        internal async override Task PreScanningAsync()
        {
            Logger.Information("Pre assessment work is starting");

            await SendRequestWithClientTagAsync();

            using (var context = await GetPnPContextAsync())
            {
                bool usesApplicationPermissions = await context.GetMicrosoft365Admin().AccessTokenUsesApplicationPermissionsAsync();
                AddToCache(UsesApplicationPermissons, usesApplicationPermissions.ToString());

                if (usesApplicationPermissions)
                {
                    bool hasSitesFullControlAll = await context.GetMicrosoft365Admin().AccessTokenHasRoleAsync("Sites.FullControl.All");
                    AddToCache(HasSitesFullControlAll, hasSitesFullControlAll.ToString());

                    if (!hasSitesFullControlAll)
                    {
                        bool hasSitesManageAll = await context.GetMicrosoft365Admin().AccessTokenHasRoleAsync("Sites.Manage.All");
                        AddToCache(HasPermissionsToReadWorkflowData, hasSitesManageAll.ToString());
                    }
                    else
                    {
                        AddToCache(HasPermissionsToReadWorkflowData, true.ToString());
                    }
                }
                else
                {
                    bool hasAllSitesFullControl = await context.GetMicrosoft365Admin().AccessTokenHasScopeAsync("AllSites.FullControl");
                    if (!hasAllSitesFullControl)
                    {
                        bool hasAllSitesManage = await context.GetMicrosoft365Admin().AccessTokenHasScopeAsync("AllSites.Manage");
                        AddToCache(HasPermissionsToReadWorkflowData, hasAllSitesManage.ToString());
                    }
                    else
                    {
                        AddToCache(HasPermissionsToReadWorkflowData, true.ToString());
                    }
                }
            }

            if (!Options.DeepScan || (GetBoolFromCache(UsesApplicationPermissons) && !GetBoolFromCache(HasSitesFullControlAll)))
            {
                Logger.Information("No DeepScan selected or Application Permissions without Sites.FullControl.All used ==> not using exact content type file counts");
            }

            if (!GetBoolFromCache(HasPermissionsToReadWorkflowData))
            {
                Logger.Information("No adequate permissions were provided to check for workflow usage. Minimal role is Sites.Manage.All when application permissions, when using delegated the minimal scope is AllSites.Manage");
            }

            //if (GetBoolFromCache(UsesApplicationPermissons))
            //{
            //    Logger.Warning("Flow instance counts will not be available when using application permissions");
            //}

            Logger.Information("Pre assessment work done");
        }

        internal async override Task PostScanningAsync()
        {
            Logger.Information("Post assessment work is starting");
            using (var dbContext = new ScanContext(ScanId))
            {

                foreach (var contentTypeOverview in dbContext.SyntexContentTypeOverview.Where(p => p.ScanId == ScanId))
                {
                    // Count the content type instances
                    contentTypeOverview.ListCount = dbContext.SyntexContentTypes.Count(p => p.ScanId == ScanId && p.ContentTypeId == contentTypeOverview.ContentTypeId);

                    // Get descriptive statistics for the number of files of a given content type
                    var usage = CountFilesUsingContentType(dbContext, contentTypeOverview.ContentTypeId);
                    contentTypeOverview.ItemCount = usage.Count;
                    contentTypeOverview.ItemCountMin = NaNToDouble(usage.Min);
                    contentTypeOverview.ItemCountMax = NaNToDouble(usage.Max);
                    contentTypeOverview.ItemCountMean = NaNToDouble(usage.Mean);
                    contentTypeOverview.ItemCountMedian = NaNToDouble(usage.Median);
                    contentTypeOverview.ItemCountLowerQuartile = NaNToDouble(usage.LowerQuartile);
                    contentTypeOverview.ItemCountUpperQuartile = NaNToDouble(usage.UpperQuartile);
                    contentTypeOverview.ItemCountStandardDeviation = NaNToDouble(usage.StandardDeviation);
                }

                // Populate some extra columns to speed up the PowerBI report
                foreach(var syntexList in dbContext.SyntexLists)
                {
                    // If no deepscan was done then we don't have individual document/folder counts, so fall back to item count
                    int documentCount = 0;
                    if (Options.DeepScan)
                    {
                        documentCount = syntexList.DocumentCount;
                    }
                    else
                    {
                        documentCount = syntexList.ItemCount;
                    }

                    if (documentCount <= 99)
                    {
                        syntexList.LibrarySize = "Small";
                    }
                    else if (documentCount <= 999)
                    {
                        syntexList.LibrarySize = "Medium";
                    }
                    else
                    {
                        syntexList.LibrarySize = "Large";
                    }

                    if (syntexList.DocumentCount > 0 && syntexList.FolderCount > 0)
                    {
                        syntexList.AverageDocumentsPerFolder = syntexList.DocumentCount / syntexList.FolderCount;
                    }
                    else
                    {
                        syntexList.AverageDocumentsPerFolder = syntexList.DocumentCount;
                    }

                    syntexList.UsesCustomColumns = syntexList.FieldCount > 0;
                }

                // save all changes per content type
                await dbContext.SaveChangesAsync();
            }

            Logger.Information("Post assessment work done");
        }

        private double NaNToDouble(double input)
        {
            if (input.Equals(double.NaN))
            {
                return -1;
            }

            return input;
        }

        private async Task CalculateContentTypeCountsAsync(PnPContext context, List<SyntexContentType> contentTypes, List<SyntexList> syntexLists)
        {
            if (!Options.DeepScan || (GetBoolFromCache(UsesApplicationPermissons) && !GetBoolFromCache(HasSitesFullControlAll)))
            {
                return;
            }

            List<Guid> uniqueListIds = new();
            foreach (var contentType in contentTypes)
            {
                if (!uniqueListIds.Contains(contentType.ListId))
                {
                    uniqueListIds.Add(contentType.ListId);
                }
            }

            // Issue a search request per list, refine the results on contenttypeid
            // We're not batching search requests to ensure indiviudal request throttling is handled correctly
            foreach (var listId in uniqueListIds)
            {
                var result = await context.Web.SearchAsync(new SearchOptions($"listid:{listId} path:\"{context.Uri}\"")
                {
                    RowLimit = 0,
                    RowsPerPage = 0,
                    SortProperties = new List<SortOption>() { new SortOption("DocId") },
                    RefineProperties = new List<string> { "contenttypeid", "compliancetag" },
                    ClientType = "PnPMicrosoft365Scanner"
                });
                
                if (result.Refinements.Count > 0)
                {
                    if (result.Refinements.ContainsKey("contenttypeid"))
                    {
                        foreach (var refinementResult in result.Refinements["contenttypeid"])
                        {
                            var contentTypeId = IdFromListContentType(refinementResult.Value);
                            var contentTypeToUpdate = contentTypes.FirstOrDefault(p => p.ListId == listId && p.ContentTypeId == contentTypeId);
                            if (contentTypeToUpdate != null && (int)refinementResult.Count > 0)
                            {
                                contentTypeToUpdate.ItemCount = (int)refinementResult.Count;
                            }

                            if (contentTypeId.StartsWith(BuiltInContentTypes.Folder) || contentTypeId.StartsWith(BuiltInContentTypes.Document))
                            {
                                var listToUpdate = syntexLists.FirstOrDefault(p => p.ListId == listId);
                                if (listToUpdate != null && (int)refinementResult.Count > 0)
                                {
                                    if (contentTypeId.StartsWith(BuiltInContentTypes.Folder))
                                    {
                                        listToUpdate.FolderCount += (int)refinementResult.Count;
                                    } 
                                    else if (contentTypeId.StartsWith(BuiltInContentTypes.Document))
                                    {
                                        listToUpdate.DocumentCount += (int)refinementResult.Count;
                                    }
                                }
                            }
                        }
                    }

                    if (result.Refinements.ContainsKey("compliancetag"))
                    {
                        foreach (var refinementResult in result.Refinements["compliancetag"])
                        {
                            var label = refinementResult.Value;

                            var listToUpdate = syntexLists.FirstOrDefault(p => p.ListId == listId);
                            if (listToUpdate != null && (int)refinementResult.Count > 0)
                            {
                                listToUpdate.RetentionLabelCount += (int)refinementResult.Count;
                            }
                        }
                    }
                }
            }
        }

        private async Task CalculateCountsForRemainingListsAsync(PnPContext context, List<SyntexContentType> contentTypes, List<SyntexList> syntexLists)
        {
            if (!Options.DeepScan || (GetBoolFromCache(UsesApplicationPermissons) && !GetBoolFromCache(HasSitesFullControlAll)))
            {
                return;
            }

            List<Guid> uniqueListIds = new();
            foreach (var contentType in contentTypes)
            {
                if (!uniqueListIds.Contains(contentType.ListId))
                {
                    uniqueListIds.Add(contentType.ListId);
                }
            }

            // We're not batching search requests to ensure indiviudal request throttling is handled correctly
            foreach (var list in syntexLists)
            {
                if (!uniqueListIds.Contains(list.ListId))
                {
                    var result = await context.Web.SearchAsync(new SearchOptions($"listid:{list.ListId} path:\"{context.Uri}\"")
                    {
                        RowLimit = 0,
                        RowsPerPage = 0,
                        SortProperties = new List<SortOption>() { new SortOption("DocId") },
                        RefineProperties = new List<string> { "contenttypeid", "compliancetag" },
                        ClientType = "PnPMicrosoft365Scanner"
                    });

                    if (result.Refinements.Count > 0)
                    {
                        if (result.Refinements.ContainsKey("contenttypeid"))
                        {
                            foreach (var refinementResult in result.Refinements["contenttypeid"])
                            {
                                var contentTypeId = IdFromListContentType(refinementResult.Value);

                                if (contentTypeId == BuiltInContentTypes.Folder || contentTypeId == BuiltInContentTypes.Document)
                                {
                                    var listToUpdate = syntexLists.FirstOrDefault(p => p.ListId == list.ListId);
                                    if (listToUpdate != null && (int)refinementResult.Count > 0)
                                    {
                                        if (contentTypeId == BuiltInContentTypes.Folder)
                                        {
                                            listToUpdate.FolderCount = (int)refinementResult.Count;
                                        }
                                        else if (contentTypeId == BuiltInContentTypes.Document)
                                        {
                                            listToUpdate.DocumentCount = (int)refinementResult.Count;
                                        }
                                    }
                                }
                            }
                        }

                        if (result.Refinements.ContainsKey("compliancetag"))
                        {
                            foreach (var refinementResult in result.Refinements["compliancetag"])
                            {
                                var label = refinementResult.Value;

                                var listToUpdate = syntexLists.FirstOrDefault(p => p.ListId == list.ListId);
                                if (listToUpdate != null && (int)refinementResult.Count > 0)
                                {
                                    listToUpdate.RetentionLabelCount += (int)refinementResult.Count;
                                }
                            }
                        }
                    }
                }
            }
        }

        private ContentTypeItemUsage CountFilesUsingContentType(ScanContext dbContext, string contentTypeId)
        {
            ContentTypeItemUsage usage = new(0);

            if (!Options.DeepScan || (GetBoolFromCache(UsesApplicationPermissons) && !GetBoolFromCache(HasSitesFullControlAll)))
            {
                // Estimating content type file usage by assuming all files in a list using a content type are from that content type
                foreach (var contentType in dbContext.SyntexContentTypes.Where(p => p.ScanId == ScanId && p.ContentTypeId == contentTypeId))
                {
                    foreach (var list in dbContext.SyntexLists.Where(p => p.ScanId == ScanId && p.ListId == contentType.ListId))
                    {
                        usage.Count += list.ItemCount;
                        if (usage.ContentTypePerList.ContainsKey(contentType.ListId))
                        {
                            usage.ContentTypePerList[contentType.ListId] += list.ItemCount;
                        }
                        else
                        {
                            usage.ContentTypePerList.Add(contentType.ListId, list.ItemCount);
                        }
                    }
                }
            }
            else
            {
                foreach (var contentType in dbContext.SyntexContentTypes.Where(p => p.ScanId == ScanId && p.ContentTypeId == contentTypeId))
                {
                    usage.Count += contentType.ItemCount;
                    if (usage.ContentTypePerList.ContainsKey(contentType.ListId))
                    {
                        usage.ContentTypePerList[contentType.ListId] += contentType.ItemCount;
                    }
                    else
                    {
                        usage.ContentTypePerList.Add(contentType.ListId, contentType.ItemCount);
                    }
                }
            }

            // Calculate descriptive statistics
            var statistics = new DescriptiveStatistics(usage.ContentTypePerList.Values.ToArray());
            usage.Min = statistics.Minimum;
            usage.Max = statistics.Maximum;
            usage.Mean = statistics.Mean;
            usage.Median = usage.ContentTypePerList.Values.ToArray().Median();
            usage.StandardDeviation = statistics.StandardDeviation;
            usage.LowerQuartile = usage.ContentTypePerList.Values.ToArray().LowerQuartile();
            usage.UpperQuartile = usage.ContentTypePerList.Values.ToArray().UpperQuartile();

            return usage;
        }

        //private async Task ScanForPowerAutomateFlowsAsync(PnPContext context, List<IList> syntexListInstances, List<SyntexList> syntexLists)
        //{
        //    if (!GetBoolFromCache(UsesApplicationPermissons))
        //    {
        //        var batch = context.NewBatch();
        //        Dictionary<Guid, IEnumerableBatchResult<IFlowInstance>> batchResults = new();

        //        foreach (var list in syntexLists)
        //        {
        //            var pnpList = syntexListInstances.FirstOrDefault(p => p.Id == list.ListId);
        //            if (pnpList != null)
        //            {
        //                batchResults.Add(list.ListId, await pnpList.GetFlowInstancesBatchAsync(batch));
        //            }
        //        }

        //        // Execute the batch
        //        var batchError = await context.ExecuteAsync(batch, false);

        //        foreach (var flowBatchResult in batchResults)
        //        {
        //            if (flowBatchResult.Value.IsAvailable)
        //            {
        //                var syntexList = syntexLists.FirstOrDefault(p => p.ListId == flowBatchResult.Key);
        //                if (syntexList != null)
        //                {
        //                    syntexList.FlowInstanceCount = flowBatchResult.Value.Count;
        //                }
        //            }
        //        }
        //    }
        //}

        private async Task ScanForListWorkflowAsync(List<SyntexList> syntexLists)
        {
            if (!GetBoolFromCache(HasPermissionsToReadWorkflowData))
            {
                return;
            }

            try
            {
                using (var context = GetClientContext())
                {
                    var servicesManager = new WorkflowServicesManager(context, context.Web);
                    var subscriptionService = servicesManager.GetWorkflowSubscriptionService();
                    var subscriptions = subscriptionService.EnumerateSubscriptions();
                    context.Load(subscriptions);

                    await context.ExecuteQueryRetryAsync();

                    foreach (var listSubscription in subscriptions)
                    {
                        if (Guid.TryParse(GetWorkflowProperty(listSubscription, "Microsoft.SharePoint.ActivationProperties.ListId"), out Guid associatedListIdValue))
                        {
                            var inScopeList = syntexLists.FirstOrDefault(p => p.ListId == associatedListIdValue);
                            if (inScopeList != null)
                            {
                                inScopeList.WorkflowInstanceCount++;
                            }
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                Logger.Warning(ex, "Failed to read workflow 2013 data for {Web}", WebUrl);
            }
        }

        private async Task CaptureSyntexModelUsageDataAsync(PnPContext context, List<SyntexModelUsage> syntexModelUsage)
        {
            // Load the model usage list with the needed properties
            var modelUsageList = await context.Web.Lists.GetByServerRelativeUrlAsync($"{context.Uri.PathAndQuery}/ModelUsage", 
                                                                                     p => p.Fields.QueryProperties(p => p.InternalName,
                                                                                                                   p => p.FieldTypeKind,
                                                                                                                   p => p.TypeAsString,
                                                                                                                   p => p.Title));
            if (modelUsageList != null)
            {
                // Use a CAML query and LoadListDataAsStreamAsync approach as potentially there can be a lot of models with a lot of linked lists
                string viewXml = @"<View>
                                    <ViewFields>
                                      <FieldRef Name='Title' />
                                      <FieldRef Name='ModelUsageTargetSiteID' />
                                      <FieldRef Name='ModelUsageTargetWebID' />
                                      <FieldRef Name='ModelUsageTargetListID' />
                                      <FieldRef Name='ModelUsageClassifiedItemCount' />
                                      <FieldRef Name='ModelUsageNotProcessedItemCount' />
                                      <FieldRef Name='ModelUsageAverageConfidenceScore' />
                                    </ViewFields>
                                    <OrderBy Override='TRUE'><FieldRef Name= 'ID' Ascending= 'FALSE' /></OrderBy>
                                   </View>";

                // Load all the needed data using paged requests
                bool paging = true;
                string nextPage = null;
                while (paging)
                {
                    // Clear the previous page (if any), no point in keeping all model usage data in memory
                    modelUsageList.Items.Clear();

                    var output = await modelUsageList.LoadListDataAsStreamAsync(new PnP.Core.Model.SharePoint.RenderListDataOptions()
                    {
                        ViewXml = viewXml,
                        RenderOptions = RenderListDataOptionsFlags.ListData,
                        Paging = nextPage ?? null,
                    }).ConfigureAwait(false);

                    if (output.ContainsKey("NextHref"))
                    {
                        nextPage = output["NextHref"].ToString().Substring(1);
                    }
                    else
                    {
                        paging = false;
                    }

                    // Iterate over the retrieved list items within this page
                    foreach (var listItem in modelUsageList.Items.AsRequested())
                    {
                        // Only capture data for classifiers when linked to a list
                        if (listItem.Values["ModelUsageTargetListID"] != null && !string.IsNullOrEmpty(listItem.Values["ModelUsageTargetListID"].ToString()))
                        {
                            // Only store data if we've valid target information
                            if (listItem.Values["ModelUsageTargetSiteID"] != null && Guid.TryParse(listItem.Values["ModelUsageTargetSiteID"].ToString(), out Guid targetSiteId) &&
                                listItem.Values["ModelUsageTargetWebID"] != null && Guid.TryParse(listItem.Values["ModelUsageTargetWebID"].ToString(), out Guid targetWebId) &&
                                Guid.TryParse(listItem.Values["ModelUsageTargetListID"].ToString(), out Guid targetListId))
                            {

                                SyntexModelUsage syntexModelUsageRow = new()
                                {
                                    ScanId = ScanId,
                                    SiteUrl = SiteUrl,
                                    WebUrl = WebUrl,
                                    Classifier = listItem.Values["Title"].ToString(),
                                    TargetSiteId = targetSiteId,
                                    TargetWebId = targetWebId,
                                    TargetListId = targetListId
                                };

                                // Store counts 
                                if (listItem.Values["ModelUsageClassifiedItemCount"] != null && 
                                    int.TryParse(listItem.Values["ModelUsageClassifiedItemCount"].ToString(), out int modelUsageClassifiedItemCount))
                                {
                                    syntexModelUsageRow.ClassifiedItemCount = modelUsageClassifiedItemCount;
                                }

                                if (listItem.Values["ModelUsageNotProcessedItemCount"] != null &&
                                    int.TryParse(listItem.Values["ModelUsageNotProcessedItemCount"].ToString(), out int modelUsageNotProcessedItemCount))
                                {
                                    syntexModelUsageRow.NotProcessedItemCount = modelUsageNotProcessedItemCount;
                                }

                                if (listItem.Values["ModelUsageAverageConfidenceScore"] != null &&
                                    double.TryParse(listItem.Values["ModelUsageAverageConfidenceScore"].ToString(), out double modelUsageAverageConfidenceScore))
                                {
                                    syntexModelUsageRow.AverageConfidenceScore = modelUsageAverageConfidenceScore;
                                }

                                syntexModelUsage.Add(syntexModelUsageRow);
                            }
                            else
                            {
                                Logger.Warning("No valid values for model target site, web or list id found, not storing model data for model list {ModelList}, classifier {Classifier}", $"{context.Uri.PathAndQuery}/ModelUsage", listItem.Values["Title"].ToString());
                            }
                        }
                    }
                }                
            }
        }

        private string GetWorkflowProperty(WorkflowSubscription subscription, string propertyName)
        {
            if (subscription.PropertyDefinitions.ContainsKey(propertyName))
            {
                return subscription.PropertyDefinitions[propertyName];
            }

            return "";
        }

        private SyntexList PrepareSyntexList(IList list)
        {
            SyntexList syntexList = new()
            {
                ScanId = ScanId,
                SiteUrl = SiteUrl,
                WebUrl = WebUrl,
                ListId = list.Id,
                Title = list.Title,
                ListServerRelativeUrl = list.RootFolder.ServerRelativeUrl,
                ListTemplate = (int)list.TemplateType,
                ListTemplateString = list.TemplateType.ToString(),

                AllowContentTypes = list.ContentTypesEnabled,
                ContentTypeCount = list.ContentTypesEnabled ? list.ContentTypes.AsRequested().Count() : 0,
                ListExperienceOptions = list.ListExperience.ToString(),

                ItemCount = list.ItemCount,
                Created = list.Created,
                LastChanged = list.LastItemUserModifiedDate,
                LastChangedYear = list.LastItemUserModifiedDate.Year,
                LastChangedMonth = list.LastItemUserModifiedDate.Month,
                LastChangedMonthString = ToMonthString(list.LastItemUserModifiedDate),
                LastChangedQuarter = ToQuarterString(list.LastItemUserModifiedDate),
            };

            return syntexList;
        }

        private (SyntexContentType, string, List<SyntexContentTypeField>) PrepareSyntexContentType(IList list, IContentType contentType)
        {
            // For now we're including default content types ~ might need to become optional via a flag?
            //if (BuiltInContentTypes.Contains(IdFromListContentType(contentType.StringId)))
            //{
            //    return (null, null, null);
            //}

            List<SyntexContentTypeField> syntexContentTypeFields = new List<SyntexContentTypeField>();
            SyntexContentType syntexContentType = new()
            {
                ScanId = ScanId,
                SiteUrl = SiteUrl,
                WebUrl = WebUrl,
                ListId = list.Id,
                ContentTypeId = IdFromListContentType(contentType.StringId),  
                ListContentTypeId = contentType.StringId,
                Group = contentType.Group,
                Hidden = contentType.Hidden,
                Name = contentType.Name,
            };

            // Process the field refs
            foreach(var fieldRef in contentType.FieldLinks.AsRequested())
            {
                var field = contentType.Fields.AsRequested().FirstOrDefault(p => p.Id == fieldRef.Id);
                if (field != null)
                {
                    if (!BuiltInFields.Contains(field.Id))
                    {
                        syntexContentTypeFields.Add(new SyntexContentTypeField
                        {
                            ScanId = ScanId,
                            SiteUrl = SiteUrl,
                            WebUrl = WebUrl,
                            ListId = list.Id,
                            ContentTypeId = IdFromListContentType(contentType.StringId),
                            FieldId = field.Id,
                            InternalName = fieldRef.FieldInternalName,
                            Hidden = fieldRef.Hidden,
                            Name = fieldRef.Name,
                            Required = fieldRef.Required,
                            TypeAsString = field.TypeAsString,
                            TermSetId = field.IsPropertyAvailable(p => p.TermSetId) ? field.TermSetId : Guid.Empty,
                        });
                    }                    
                }
                else
                {
                    Logger.Warning("No Field found for FieldRef {FieldRefName} {FieldRefId} in content type {ContentTypeId} {ContentTypeName}", fieldRef.Name, fieldRef.Id, contentType.Id, contentType.Name);
                }
            }

            // Process the fields
            foreach(var field in contentType.Fields.AsRequested())
            {
                if (syntexContentTypeFields.FirstOrDefault(p => p.FieldId == field.Id) == null)
                {
                    if (!BuiltInFields.Contains(field.Id))
                    {
                        syntexContentTypeFields.Add(new SyntexContentTypeField
                        {
                            ScanId = ScanId,
                            SiteUrl = SiteUrl,
                            WebUrl = WebUrl,
                            ListId = list.Id,
                            ContentTypeId = IdFromListContentType(contentType.StringId),
                            FieldId = field.Id,
                            InternalName = field.InternalName,
                            Hidden = field.Hidden,
                            Name = field.Title,
                            Required = field.Required,
                            TypeAsString = field.TypeAsString,
                            TermSetId = field.IsPropertyAvailable(p => p.TermSetId) ? field.TermSetId : Guid.Empty,
                        });
                    }
                }
            }

            syntexContentType.FieldCount = syntexContentTypeFields.Count;

            return (syntexContentType, contentType.SchemaXml, syntexContentTypeFields);
        }

        private List<SyntexField> PrepareSyntexFields(IList list)
        {
            List<SyntexField> syntexFields = new();

            foreach (var field in list.Fields.AsRequested())
            {
                if (!BuiltInFields.Contains(field.Id))
                {
                    syntexFields.Add(new SyntexField
                    {
                        ScanId = ScanId,
                        SiteUrl = SiteUrl,
                        WebUrl = WebUrl,
                        ListId = list.Id,
                        FieldId = field.Id,
                        InternalName = field.InternalName,
                        Hidden = field.Hidden,
                        Name = field.Title,
                        Required = field.Required,
                        TypeAsString = field.TypeAsString,
                        TermSetId= field.IsPropertyAvailable(p => p.TermSetId) ? field.TermSetId : Guid.Empty,
                    });
                }
            }

            return syntexFields;
        }

        private static string IdFromListContentType(string listContentTypeId)
        {
            return listContentTypeId[0..^34];
        }

        private static bool IncludeList(IList list)
        {
            if (list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.DocumentLibrary ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.PictureLibrary ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.XMLForm ||
                list.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.MySiteDocumentLibrary)
            {
                if (!list.IsSiteAssetsLibrary && !list.IsSystemList && !list.Hidden)
                {
                    return true;
                }
            }

            return false;
        }

        private static string ToMonthString(DateTime value)
        {
            if (value == DateTime.MinValue)
            {
                return "";
            }
            else
            {
                return CultureInfo.GetCultureInfo("en").DateTimeFormat.GetAbbreviatedMonthName(value.Month);
            }
        }

        private static string ToQuarterString(DateTime value)
        {
            if (value == DateTime.MinValue)
            {
                return "";
            }
            else
            {
                if (value.Month <= 3)
                {
                    return "Q1";
                }
                else if (value.Month <= 6)
                {
                    return "Q2";
                }
                else if (value.Month <= 9)
                {
                    return "Q3";
                }
                else
                {
                    return "Q4";
                }
            }
        }

        private static (string driveId, string modelId) IsSyntexContentType(string schemaXml)
        {
            string driveId = null;
            string modelId = null;

            XmlDocument xmlDocument = new();
            xmlDocument.LoadXml(schemaXml);
            if (xmlDocument.DocumentElement != null)
            {
                XmlNode root = xmlDocument.DocumentElement;

                var nsMgr = new XmlNamespaceManager(new NameTable());
                nsMgr.AddNamespace("syntex", "http://schemas.microsoft.com/sharepoint/v3/machinelearning/modelid");

                var modelDriveIdNode = root.SelectSingleNode("//ContentType/XmlDocuments/XmlDocument/syntex:ModelId/syntex:ModelDriveId", nsMgr);
                if (modelDriveIdNode != null)
                {
                    driveId = modelDriveIdNode.InnerText;
                }

                var modelObjectId = root.SelectSingleNode("//ContentType/XmlDocuments/XmlDocument/syntex:ModelId/syntex:ModelObjectId", nsMgr);
                if (modelObjectId != null)
                {
                    modelId = modelObjectId.InnerText;
                }
            }

            return (driveId, modelId);
        }

    }
}
