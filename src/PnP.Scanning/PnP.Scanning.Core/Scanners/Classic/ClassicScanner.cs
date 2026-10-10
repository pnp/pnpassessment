using EFCore.BulkExtensions;
using PnP.Core.Model;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Pipeline.Analysis;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Discovery;
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

            // Persist existence and discovery failures before feature-dependent enrichment loads.
            // Each existing TPL Web worker owns its own acquisition provider and PnP contexts.
            var discoveredPages = Options.Pages
                ? await ClassicPageDiscoveryComponent.ExecuteAsync(this).ConfigureAwait(false)
                : null;

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

            using (var context = await GetAssessmentContextAsync(options))
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
                    await ClassicPageLegacyAdapter.ExecuteAsync(this, context, csomContext, discoveredPages).ConfigureAwait(false);

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

        private Task<PnPContext> GetAssessmentContextAsync(PnPContextOptions options)
        {
            if (!Options.Pages) return GetPnPContextAsync(options);
            return ClassicAssessmentInitialization.ExecuteAsync(() => GetPnPContextAsync(options),
                new AssessmentDiscoveryWriter(ScanId), ScanId, SiteUrl, WebUrl,
                ScanManager.GetCancellationTokenSource(ScanId).Token,
                (attempt, error) => Logger.Warning(error,
                    "Response ended while initializing page assessment for {SiteUrl}{WebUrl}; retry {Attempt} of {MaxAttempts}",
                    SiteUrl, WebUrl, attempt, ClassicAssessmentInitialization.MaxAttempts - 1));
        }

        internal async override Task PreScanningAsync()
        {
            Logger.Information("Pre assessment work is starting");

            try
            {
                await SendRequestWithClientTagAsync();
            }
            catch (Exception ex) when (Options.Pages && !ScanManager.GetCancellationTokenSource(ScanId).IsCancellationRequested)
            {
                // Client-tag telemetry against the first site is not an admission gate for
                // other authorized sites. Each scheduled Web worker records its own result.
                await ClassicPageDiscoveryComponent.RecordScopeAsync(ScanId, SiteUrl, WebUrl, "Web", "Failed", ex,
                    stage: "PreScanClientTag");
                Logger.Warning(ex, "Client tag preflight failed; continuing full ASPX discovery");
            }

            if (Options.Workflow)
            {
                WorkflowManager.Instance.LoadWorkflowDefaultActions();
            }
            
            Logger.Information("Pre assessment work done");
        }

        internal async override Task PostScanningAsync()
        {

            Logger.Information("Post assessment work is starting");
            if (Options.Pages)
            {
                var verdict = await new AssessmentDiscoveryWriter(ScanId).FinalizeScanAsync(ScanId)
                    .ConfigureAwait(false);
                Logger.Information("ASPX discovery for assessment {ScanId} finished with coverage verdict {Verdict}",
                    ScanId, verdict);
            }
            using (var dbContext = new ScanContext(ScanId))
            {
                // T9: before aggregating webs into site collections, roll each web's per-page
                // transformation readiness up into its ClassicWebSummary and build the scan-wide unique
                // web part inventory. The site loop below then reads the now-populated web columns.
                await StorageManager.ComputeAndStoreWebPageRollupsAsync(dbContext, ScanId);
                await StorageManager.PopulateWebPartUniqueAsync(dbContext, ScanId, ClassicPageLegacyAdapter.MappingManager);

                // Roll the publishing webs + pages up into one per-site-collection publishing-portal line
                // (parity with the legacy ModernizationPublishingSiteScanResults.csv). Reads the web summaries
                // populated during the scan, so it runs after the web-level rollups above.
                await StorageManager.PopulatePublishingSiteSummaryAsync(dbContext, ScanId);

                dbContext.ClassicSiteSummaries.AddRange(ClassicPageSummaryBuilder.Sites(ScanId,
                    dbContext.ClassicWebSummaries.Where(p => p.ScanId == ScanId).ToList()
                        .Select(Pipeline.Contracts.ClassicPageSourceJson.Convert<Pipeline.Contracts.ClassicWebSummaryRow>))
                    .Select(Pipeline.Contracts.ClassicPageSourceJson.Convert<ClassicSiteSummary>));

                // Persist the changes
                await dbContext.SaveChangesAsync();

                // Audit log usage collection — replaces the legacy SharePoint Search page-usage pipeline.
                // Skipped when --skipusageinformation is passed, matching the semantics of the old flag.
                if (!Options.SkipUsageInformation)
                {
                    var authManager = ScanManager.GetScanAuthenticationManager(ScanId);
                    var scan = dbContext.Scans.FirstOrDefault(p => p.ScanId == ScanId);
                    string environment = scan?.CLIEnvironment ?? PnP.Core.Services.Microsoft365Environment.Production.ToString();
                    int windowDays = Options.AuditLogWindowDays;
                    DateTime windowEnd = DateTime.UtcNow;
                    DateTime windowStart = windowEnd.AddDays(-windowDays);
                    // Use the scan's live cancellation token so pause/abort is honoured during the
                    // potentially long audit log query (up to 45 min per chunk).
                    var ct = ScanManager.GetCancellationTokenSource(ScanId).Token;
                    try
                    {
                        await StorageManager.CollectAndStoreAuditLogUsageAsync(dbContext, ScanId, authManager, environment, windowStart, windowEnd, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Warning("Audit log collection was cancelled for scan {ScanId}", ScanId);
                    }
                    catch (Exception ex)
                    {
                        // Log and continue — a failure here (e.g. BulkInsert / OOM / Graph API error) must not
                        // fail the entire post-scan phase and discard site summaries already committed.
                        Logger.Warning(ex, "Audit log collection failed for scan {ScanId}: {Error}", ScanId, ex.Message);

                        // Write a site-level error row per scanned site so classicpageauditusage.csv
                        // is generated and the customer can see what went wrong instead of finding a missing file.
                        try
                        {
                            // Reuse the outer dbContext — opening a second ScanContext while the outer
                            // connection is still alive causes "database is locked" in SQLite DELETE mode.
                            var siteUrls = dbContext.ClassicSiteSummaries
                                .Where(p => p.ScanId == ScanId)
                                .Select(p => p.SiteUrl)
                                .ToList();
                            var errorRecords = siteUrls.Select(siteUrl => new PnP.Scanning.Core.Storage.ClassicPageAuditUsage
                            {
                                ScanId = ScanId,
                                SiteUrl = siteUrl,
                                WebUrl = "/",
                                PageUrl = siteUrl,
                                AuditWindowStart = windowStart,
                                AuditWindowEnd = windowEnd,
                                QueryStatus = "error",
                                SkipReason = $"{ex.GetType().Name}: {ex.Message[..Math.Min(200, ex.Message.Length)]}",
                            }).ToList();
                            if (errorRecords.Count > 0)
                                await dbContext.BulkInsertAsync(errorRecords);
                        }
                        catch (Exception writeEx)
                        {
                            Logger.Warning(writeEx, "Could not write error rows for scan {ScanId}", ScanId);
                        }
                    }
                }
            }

            Logger.Information("Post assessment work done");
        }

        internal static SiteType GetSiteType(string webTemplate) => ClassicSiteRules.GetSiteType(webTemplate);

    }
}
