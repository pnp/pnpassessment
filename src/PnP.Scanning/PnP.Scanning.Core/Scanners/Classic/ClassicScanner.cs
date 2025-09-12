using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using System.Linq.Expressions;

namespace PnP.Scanning.Core.Scanners
{
    internal class ClassicScanner : ScannerBase
    {
        public ClassicScanner(ScanManager scanManager, StorageManager storageManager, IPnPContextFactory pnpContextFactory,
                               Guid scanId, string siteUrl, string webUrl, string webTemplate, ClassicOptions options) :
                               base(scanManager, storageManager, pnpContextFactory, scanId, siteUrl, webUrl, webTemplate)
        {
            Options = options;
        }

        internal ClassicOptions Options { get; set; }

        internal async override Task ExecuteAsync()
        {
            Logger.Information("Starting Classic assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

            // Define extra Web/Site data that we want to load when the context is inialized
            // This will not require extra server roundtrips
            PnPContextOptions options = new()
            {
                AdditionalSitePropertiesOnCreate = new Expression<Func<ISite, object>>[]
                {
                    w => w.RootWeb.QueryProperties(p => p.ContentTypes.QueryProperties(p => p.StringId, p => p.Name))
                },
                AdditionalWebPropertiesOnCreate = new Expression<Func<IWeb, object>>[]
                {   
                    w => w.LastItemUserModifiedDate,                 
                    w => w.Lists.QueryProperties(r => r.Title, 
                                                 r => r.Hidden,
                                                 r => r.DefaultViewUrl,
                                                 r => r.TemplateType,
                                                 r => r.TemplateFeatureId,
                                                 r => r.ListExperience,
                                                 r => r.ItemCount,
                                                 r => r.LastItemUserModifiedDate,
                                                 r => r.DocumentTemplate,
                                                 r => r.RootFolder.QueryProperties(p => p.ServerRelativeUrl),
                                                 r => r.ContentTypes.QueryProperties(p => p.Id, p => p.DocumentTemplateUrl),
                                                 r => r.Fields.QueryProperties(p => p.InternalName, p => p.FieldTypeKind, p => p.TypeAsString, p => p.Title),
                                                 r => r.UserCustomActions)
                }
            };

            if (Options.Pages)
            {
                // Also load site/web feature collections
                options.AdditionalSitePropertiesOnCreate = options.AdditionalSitePropertiesOnCreate.Union(new Expression<Func<ISite, object>>[] { w => w.Features.QueryProperties(p => p.DefinitionId) });
                options.AdditionalWebPropertiesOnCreate = options.AdditionalWebPropertiesOnCreate.Union(new Expression<Func<IWeb, object>>[] { w => w.Features.QueryProperties(p => p.DefinitionId) });
            }

            if (Options.Extensibility)
            {
                options.AdditionalSitePropertiesOnCreate = options.AdditionalSitePropertiesOnCreate.Union(new Expression<Func<ISite, object>>[] { w => w.UserCustomActions });
                options.AdditionalWebPropertiesOnCreate = options.AdditionalWebPropertiesOnCreate.Union(new Expression<Func<IWeb, object>>[] { w => w.UserCustomActions,
                                                                                                                                               w => w.AlternateCssUrl,
                                                                                                                                               w => w.CustomMasterUrl,
                                                                                                                                               w => w.MasterUrl });
            }

            using (var context = await GetPnPContextAsync(options))
            using (var csomContext = GetClientContext(context))
            {
                if (Options.Workflow)
                {
                    Logger.Information("Starting classic Workflow assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

                    // Call the workflow scan component
                    await WorkflowScanComponent.ExecuteAsync(new WorkflowOptions { Mode = Mode.Workflow.ToString(), Analyze = true }, 
                                                             this, context, csomContext).ConfigureAwait(false);

                    // Store workflow summary data
                    HashSet<string> remediationCodes = new()
                    {
                        RemediationCodes.WF1.ToString()
                    };
                    await StorageManager.StoreWorkflowSummaryAsync(ScanId, SiteUrl, WebUrl, WebTemplate, context, remediationCodes).ConfigureAwait(false);

                    Logger.Information("Classic Workflow assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
                }

                if (Options.InfoPath)
                {
                    Logger.Information("Starting classic InfoPath assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

                    // Call the InfoPath scan component
                    await InfoPathScanComponent.ExecuteAsync(this, context, csomContext).ConfigureAwait(false);
                    
                    Logger.Information("Classic InfoPath assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
                }

                if (Options.Pages)
                {
                    Logger.Information("Starting classic Pages assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

                    // Call the Page scan component
                    await PageScanComponent.ExecuteAsync(this, context, csomContext).ConfigureAwait(false);

                    Logger.Information("Classic Pages assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
                }

                if (Options.Lists)
                {
                    Logger.Information("Starting classic Lists assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

                    // Call the List scan component
                    await ListScanComponent.ExecuteAsync(this, context, csomContext).ConfigureAwait(false);

                    Logger.Information("Classic Lists assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
                }

                if (Options.Extensibility)
                {
                    Logger.Information("Starting Extensibility assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

                    // Call the UserCustomAction scan component
                    Logger.Information("Starting Extensibility:UserCustomAction assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);
                    await UserCustomActionScanComponent.ExecuteAsync(this, context, csomContext).ConfigureAwait(false);
                    Logger.Information("Classic Extensibility:UserCustomAction assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
                    
                    // Call the Extensibility scan component
                    Logger.Information("Starting Extensibility:Core assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);
                    await ExtensibilityScanComponent.ExecuteAsync(this, context, csomContext).ConfigureAwait(false);
                    Logger.Information("Classic Extensibility:Core assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);

                    Logger.Information("Classic Extensibility assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
                }

                if (Options.AzureACS)
                {
                    //Logger.Information("Starting Azure ACS assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

                    // TODO

                    //Logger.Information("Classic Azure ACS assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
                }
                
                if (Options.SharePointAddIns)
                {
                    //Logger.Information("Starting SharePoint AddIns assessment of web {SiteUrl}{WebUrl}", SiteUrl, WebUrl);

                    // TODO

                    //Logger.Information("Classic SharePoint AddIns assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
                }

                // Store site summary
                await StorageManager.StoreSiteSummaryAsync(ScanId, SiteUrl, WebUrl, WebTemplate, context).ConfigureAwait(false);

            }

            Logger.Information("Classic assessment of web {SiteUrl}{WebUrl} done", SiteUrl, WebUrl);
        }

        internal async override Task PreScanningAsync()
        {
            Logger.Information("Pre assessment work is starting");

            await SendRequestWithClientTagAsync();

            if (Options.Workflow)
            {
                WorkflowManager.Instance.LoadWorkflowDefaultActions();
            }
            
            Logger.Information("Pre assessment work done");
        }

        internal async override Task PostScanningAsync()
        {

            Logger.Information("Post assessment work is starting");
            using (var dbContext = new ScanContext(ScanId))
            {
                // Iterate over the sites to populate the classic site collection overview table
                string lastSiteUrl = null;
                HashSet<string> webTemplates = null;
                HashSet<string> remediationCodes = null;
                ClassicSiteSummary classicSiteCollection = null;
                
                foreach (var web in dbContext.ClassicWebSummaries.Where(p => p.ScanId == ScanId).OrderBy(p => p.SiteUrl).ThenBy(p => p.WebUrl))
                {
                    if (lastSiteUrl == null || lastSiteUrl != web.SiteUrl)
                    {
                        // We're starting to process a new site collection, so store the previous one
                        if (lastSiteUrl != null)
                        {
                            AddClassicSiteCollection(dbContext, webTemplates, remediationCodes, classicSiteCollection);
                        }

                        // Initialize variables for the new site collection
                        classicSiteCollection = new ClassicSiteSummary
                        {
                            ScanId = ScanId,
                            SiteUrl = web.SiteUrl,
                        };

                        lastSiteUrl = web.SiteUrl;
                        webTemplates = new HashSet<string>();
                        remediationCodes = new HashSet<string>();
                    }

                    if (web.WebUrl != "/")
                    {
                        // We're processing a subweb
                        classicSiteCollection.SubWebCount++;

                        // Check the sub web depth
                        int depth = CountCharsUsingForeachSpan(web.WebUrl, '/');
                        if (depth > classicSiteCollection.SubWebDepth)
                        {
                            classicSiteCollection.SubWebDepth = depth;
                        }

                        // maintain list of unique sub web templates
                        webTemplates.Add(web.Template);

                        if (web.LastItemUserModifiedDate > classicSiteCollection.LastItemUserModifiedDate)
                        {
                            classicSiteCollection.LastItemUserModifiedDate = web.LastItemUserModifiedDate;
                        }
                    }
                    else
                    {
                        classicSiteCollection.RootWebTemplate = web.Template;
                        classicSiteCollection.LastItemUserModifiedDate = web.LastItemUserModifiedDate;
                    }

                    classicSiteCollection.ClassicLists += web.ClassicLists;
                    classicSiteCollection.ModernLists += web.ModernLists;
                    
                    classicSiteCollection.ClassicPages += web.ClassicPages;
                    classicSiteCollection.ModernPages += web.ModernPages;
                    classicSiteCollection.ClassicWikiPages += web.ClassicWikiPages;
                    classicSiteCollection.ClassicASPXPages += web.ClassicASPXPages;
                    classicSiteCollection.ClassicBlogPages += web.ClassicBlogPages;
                    classicSiteCollection.ClassicWebPartPages += web.ClassicWebPartPages;
                    classicSiteCollection.ClassicPublishingPages += web.ClassicPublishingPages;

                    classicSiteCollection.ClassicWorkflows += web.ClassicWorkflows;

                    classicSiteCollection.ClassicInfoPathForms += web.ClassicInfoPathForms;

                    classicSiteCollection.ClassicExtensibilities += web.ClassicExtensibilities;
                    classicSiteCollection.SharePointAddIns += web.SharePointAddIns;
                    classicSiteCollection.AzureACSPrincipals += web.AzureACSPrincipals;

                    AggregateRemediationCodes(remediationCodes, web.AggregatedRemediationCodes);
                }

                // Store the last site collection
                AddClassicSiteCollection(dbContext, webTemplates, remediationCodes, classicSiteCollection);
                
                // Persist the changes
                await dbContext.SaveChangesAsync();
            }

            Logger.Information("Post assessment work done");
        }

        private static void AddClassicSiteCollection(ScanContext dbContext, HashSet<string> webTemplates, HashSet<string> remediationCodes, ClassicSiteSummary classicSiteCollection)
        {
            // Get the unique list of sub web templates
            if (webTemplates.Count > 0)
            {
                classicSiteCollection.SubWebTemplates = string.Join(",", webTemplates);
            }

            if (remediationCodes.Count > 0)
            {
                classicSiteCollection.AggregatedRemediationCodes = string.Join(",", remediationCodes);
            }

            // Persist the previously collected site collection data
            dbContext.ClassicSiteSummaries.Add(classicSiteCollection);
        }

        internal static SiteType GetSiteType(string webTemplate)
        {
            return webTemplate.ToUpper() switch
            {
                // Modern Communication site or Topic Center
                "SITEPAGEPUBLISHING#0" => SiteType.Communication,
                // Modern team site without group
                "STS#3" => SiteType.Modern,
                // Modern team site with group
                "GROUP#0" => SiteType.Modern,
                // Microsoft Syntex Content Center
                "CONTENTCTR#0" => SiteType.Modern,
                // Site linked to Team channel, version 1
                "TEAMCHANNEL#0" => SiteType.Modern,
                // Site linked to Team channel, version 2
                "TEAMCHANNEL#1" => SiteType.Modern,
                // Tenant Admin Center site
                "TENANTADMIN#0" => SiteType.Modern,
                // Publishing portal
                "BLANKINTERNETCONTAINER#0" => SiteType.Publishing,
                // Publishing site
                "CMSPUBLISHING#0" => SiteType.Publishing,
                // Publishing site
                "BLANKINTERNET#0" => SiteType.Publishing,
                // Press Releases Site
                "BLANKINTERNET#1" => SiteType.Publishing,
                // Publishing site with workflow
                "BLANKINTERNET#2" => SiteType.Publishing,
                // Enterprise Wiki
                "ENTERWIKI#0" => SiteType.Publishing,
                // Enterprise Search Center
                "SRCHCEN#0" => SiteType.Publishing,
                // Site Directory
                "SPSSITES#0" => SiteType.Publishing,
                // News Home Site
                "SPSNHOME#0" => SiteType.Publishing,
                // Product Catalog
                "PRODUCTCATALOG#0" => SiteType.Publishing,
                // Report Center
                "SPSREPORTCENTER#0" => SiteType.Publishing,
                // Topic Area Template
                "SPSTOPIC#0" => SiteType.Publishing,
                // Blog
                "BLOG#0" => SiteType.Blog,
                // Everything else
                _ => SiteType.Classic,
            };
        }

        private static void AggregateRemediationCodes(HashSet<string> remediationCodes, string input)
        {
            var split = input?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (split != null)
            {
                foreach (var code in split)
                {
                    remediationCodes.Add(code);
                }
            }
        }

        private static int CountCharsUsingForeachSpan(string source, char toFind)
        {
            int count = 0;
            foreach (var c in source.AsSpan())
            {
                if (c == toFind)
                {
                    count++;
                }
            }
            return count;
        }
    }
}
