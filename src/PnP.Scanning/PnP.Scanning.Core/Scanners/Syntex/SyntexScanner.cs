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

namespace PnP.Scanning.Core.Scanners
{
    internal class SyntexScanner : ScannerBase
    {
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

        private class ContentTypeFileUsage
        {
            internal ContentTypeFileUsage(int count)
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


        public SyntexScanner(ScanManager scanManager, StorageManager storageManager, IPnPContextFactory pnpContextFactory, Guid scanId, string siteUrl, string webUrl, SyntexOptions options) : base(scanManager, storageManager, pnpContextFactory, scanId, siteUrl, webUrl)
        {
            Options = options;
        }

        internal SyntexOptions Options { get; set; }


        internal async override Task ExecuteAsync()
        {
            Logger.Information("Starting Syntex scan of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

            // Define extra Web/Site data that we want to load when the context is inialized
            // This will not require extra server roundtrips
            PnPContextOptions options = new()
            {
                AdditionalWebPropertiesOnCreate = new Expression<Func<IWeb, object>>[]
                {
                    //w => w.Fields, 
                    //w => w.Features,
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

                List<SyntexList> syntexLists = new();
                List<SyntexContentType> syntexContentTypes = new();
                List<SyntexContentTypeField> syntexContentTypeFields = new();
                List<SyntexField> syntexFields = new();

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
                        (string? driveId, string? modelId) = IsSyntexContentType(contentType.SchemaXml);

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

                // Persist the gathered data
                await StorageManager.StoreSyntexInformationAsync(ScanId, syntexLists, syntexContentTypes, syntexContentTypeFields, syntexFields);
            }

            Logger.Information("Syntex scan of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
        }

        internal async override Task PreScanningAsync()
        {
            Logger.Information("Pre scanning work is starting");

            using (var context = await GetPnPContextAsync())
            {
                bool usesApplicationPermissions = await context.GetMicrosoft365Admin().AccessTokenUsesApplicationPermissionsAsync();
                AddToCache("UsesApplicationPermissons", usesApplicationPermissions.ToString());

                if (usesApplicationPermissions)
                {
                    // todo: if needed check here for specific permissions needed and cache them
                    // so they can be used at no cost in the actual scan code
                }
                else
                {
                    // todo: if needed check here for specific permissions needed and cache them
                    // so they can be used at no cost in the actual scan code
                }
            }

            Logger.Information("Pre scanning work done");
        }

        internal async override Task PostScanningAsync()
        {
            Logger.Information("Post scanning work is starting");
            using (var context = GetClientContext())
            {
                using (var dbContext = new ScanContext(ScanId))
                {
                    foreach (var contentTypeOverview in dbContext.SyntexContentTypeOverview.Where(p => p.ScanId == ScanId))
                    {
                        // Count the content type instances
                        contentTypeOverview.ListCount = dbContext.SyntexContentTypes.Count(p => p.ScanId == ScanId && p.ContentTypeId == contentTypeOverview.ContentTypeId);
                        
                        // Get descriptive statistics for the number of files of a given content type
                        var usage = await CountFilesUsingContentTypeAsync(context, contentTypeOverview.ContentTypeId);
                        contentTypeOverview.FileCount = usage.Count;
                        contentTypeOverview.FileCountMin = NaNToDouble(usage.Min);
                        contentTypeOverview.FileCountMax = NaNToDouble(usage.Max);
                        contentTypeOverview.FileCountMean = NaNToDouble(usage.Mean);
                        contentTypeOverview.FileCountMedian = NaNToDouble(usage.Median);
                        contentTypeOverview.FileCountLowerQuartile = NaNToDouble(usage.LowerQuartile);
                        contentTypeOverview.FileCountUpperQuartile = NaNToDouble(usage.UpperQuartile);
                        contentTypeOverview.FileCountStandardDeviation = NaNToDouble(usage.StandardDeviation);

                        if (usage.Count > 0)
                        {
                            foreach (var list in usage.ContentTypePerList)
                            {
                                var contentTypeListToUpdate = dbContext.SyntexContentTypes.FirstOrDefault(p => p.ScanId == ScanId && p.ContentTypeId == contentTypeOverview.ContentTypeId && p.ListId == list.Key);
                                if (contentTypeListToUpdate != null)
                                {
                                    contentTypeListToUpdate.FileCount = (int)list.Value;
                                }
                            }
                        }

                        // save all changes per content type
                        await dbContext.SaveChangesAsync();
                    }

                }
            }
            Logger.Information("Post scanning work done");
        }

        private double NaNToDouble(double input)
        {
            if (input.Equals(double.NaN))
            {
                return -1;
            }

            return input;
        }

        private async Task<ContentTypeFileUsage> CountFilesUsingContentTypeAsync(Microsoft.SharePoint.Client.ClientContext context, string contentTypeId)
        {
            List<string> propertiesToRetrieve = new()
            {
                "ListId",
                "UniqueId",
            };

            var results = await SearchAsync(context.Web, $"contenttypeid: \"{contentTypeId}*\"", propertiesToRetrieve);

            ContentTypeFileUsage usage = new(results.Count);

            if (results.Count > 0)
            {
                //Dictionary<Guid, double> contentTypePerList = new();
                foreach (var contentType in results)
                {
                    if (Guid.TryParse(contentType["ListId"], out Guid listId))
                    {
                        if (usage.ContentTypePerList.ContainsKey(listId))
                        {
                            usage.ContentTypePerList[listId]++;
                        }
                        else
                        {
                            usage.ContentTypePerList.Add(listId, 1);
                        }
                    }
                }

                var statistics = new DescriptiveStatistics(usage.ContentTypePerList.Values.ToArray());

                usage.Min = statistics.Minimum;
                usage.Max = statistics.Maximum;
                usage.Mean = statistics.Mean;
                usage.Median = usage.ContentTypePerList.Values.ToArray().Median();
                usage.StandardDeviation = statistics.StandardDeviation;
                usage.LowerQuartile = usage.ContentTypePerList.Values.ToArray().LowerQuartile();
                usage.UpperQuartile = usage.ContentTypePerList.Values.ToArray().UpperQuartile();
            }

            return usage;
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

        private (SyntexContentType?, string?, List<SyntexContentTypeField>?) PrepareSyntexContentType(IList list, IContentType contentType)
        {
            if (BuiltInContentTypes.Contains(IdFromListContentType(contentType.StringId)))
            {
                return (null, null, null);
            }

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

        //private async Task StoreSyntexInformationAsync(List<SyntexList> syntexLists, List<SyntexContentType> syntexContentTypes, List<SyntexContentTypeField> syntexContentTypeFields, List<SyntexField> syntexFields)
        //{
        //    Logger.Information("Start StoreSyntexInformationAsync for {SiteUrl}{WebUrl}", SiteUrl, WebUrl);
        //    using (var dbContext = new ScanContext(ScanId))
        //    {
        //        await dbContext.SyntexLists.AddRangeAsync(syntexLists.ToArray());
        //        await dbContext.SyntexContentTypes.AddRangeAsync(syntexContentTypes.ToArray());
        //        await dbContext.SyntexContentTypeFields.AddRangeAsync(syntexContentTypeFields.ToArray());
        //        await dbContext.SyntexFields.AddRangeAsync(syntexFields.ToArray());

        //        await dbContext.SaveChangesAsync();
        //        Logger.Information("StoreSyntexInformationAsync succeeded for {SiteUrl}{WebUrl}", SiteUrl, WebUrl);
        //    }
        //}

        //internal async Task<bool> IsContentTypeStoredAsync(string contentTypeId)
        //{
        //    using (var dbContext = new ScanContext(ScanId))
        //    {
        //        var contentType = await dbContext.SyntexContentTypes.FirstOrDefaultAsync(p => p.ScanId == ScanId && p.ContentTypeId == contentTypeId);

        //        if (contentType != null)
        //        {
        //            return true;
        //        }
        //        else
        //        {
        //            return false;
        //        }
        //    }
        //}

        //internal async Task AddToContentTypeSummaryAsync(SyntexContentTypeSummary syntexContentTypeSummary)
        //{
        //    using (var dbContext = new ScanContext(ScanId))
        //    {
        //        dbContext.ChangeTracker.QueryTrackingBehavior = Microsoft.EntityFrameworkCore.QueryTrackingBehavior.NoTracking;

        //        var contentType = await dbContext.SyntexContentTypeOverview.FirstOrDefaultAsync(p => p.ScanId == ScanId && p.ContentTypeId == syntexContentTypeSummary.ContentTypeId);
        //        if (contentType == null)
        //        {
        //            await dbContext.SyntexContentTypeOverview.AddAsync(syntexContentTypeSummary);
        //            await dbContext.SaveChangesAsync();
        //        }
        //    }
        //}

        private static string IdFromListContentType(string listContentTypeId)
        {
            return listContentTypeId[0..^34];
        }

        private static bool IncludeList(IList list)
        {
            if (list.TemplateType == ListTemplateType.DocumentLibrary ||
                list.TemplateType == ListTemplateType.PictureLibrary ||
                list.TemplateType == ListTemplateType.XMLForm ||
                list.TemplateType == ListTemplateType.MySiteDocumentLibrary)
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

        private static (string? driveId, string? modelId) IsSyntexContentType(string schemaXml)
        {
            string? driveId = null;
            string? modelId = null;

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
