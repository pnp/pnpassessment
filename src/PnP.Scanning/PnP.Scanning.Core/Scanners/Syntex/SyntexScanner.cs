using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using System.Globalization;
using System.Linq.Expressions;

namespace PnP.Scanning.Core.Scanners
{
    internal class SyntexScanner : ScannerBase
    {
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
                                                 r => r.ContentTypes.QueryProperties(c => c.Id, c => c.StringId, c=> c.Name, c => c.Hidden, c => c.Group,
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
                                (SyntexContentType syntexContentType, List<SyntexContentTypeField> syntexContentTypeFieldsCollection) = PrepareSyntexContentType(list, contentType);
                                if (syntexContentType != null && syntexContentTypeFieldsCollection != null)
                                {
                                    syntexContentTypes.Add(syntexContentType);
                                    syntexContentTypeFields.AddRange(syntexContentTypeFieldsCollection.ToArray());
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

                // Persist the gathered data
                await StoreSyntexInformationAsync(syntexLists, syntexContentTypes, syntexContentTypeFields, syntexFields);
            }

            Logger.Information("Syntex scan of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
        }

        internal async override Task PreScanningAsync()
        {
            //Logger.Information("Pre scanning work is starting");

            //AddToCache(Cache1, $"PnP Rocks! - {DateTime.Now}");

            //Logger.Information("Pre scanning work done");
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

        private (SyntexContentType?, List<SyntexContentTypeField>?) PrepareSyntexContentType(IList list, IContentType contentType)
        {
            if (BuiltInContentTypes.Contains(IdFromListContentType(contentType.StringId)))
            {
                return (null, null);
            }

            List<SyntexContentTypeField> syntexContentTypeFields = new List<SyntexContentTypeField>();
            SyntexContentType syntexContentType = new()
            {
                ScanId = ScanId,
                SiteUrl = SiteUrl,
                WebUrl = WebUrl,
                ListId = list.Id,
                ContentTypeId = IdFromListContentType(contentType.StringId),  
                Group = contentType.Group,
                Hidden = contentType.Hidden,
                Name = contentType.Name,
                IsSyntexContentType = false
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

            return (syntexContentType, syntexContentTypeFields);
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

        private async Task StoreSyntexInformationAsync(List<SyntexList> syntexLists, List<SyntexContentType> syntexContentTypes, List<SyntexContentTypeField> syntexContentTypeFields, List<SyntexField> syntexFields)
        {
            Logger.Information("Start StoreSyntexInformationAsync for {SiteUrl}{WebUrl}", SiteUrl, WebUrl);
            using (var dbContext = new ScanContext(ScanId))
            {
                await dbContext.SyntexLists.AddRangeAsync(syntexLists.ToArray());
                await dbContext.SyntexContentTypes.AddRangeAsync(syntexContentTypes.ToArray());
                await dbContext.SyntexContentTypeFields.AddRangeAsync(syntexContentTypeFields.ToArray());
                await dbContext.SyntexFields.AddRangeAsync(syntexFields.ToArray());

                await dbContext.SaveChangesAsync();
                Logger.Information("StoreSyntexInformationAsync succeeded for {SiteUrl}{WebUrl}", SiteUrl, WebUrl);
            }
        }


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

    }
}
