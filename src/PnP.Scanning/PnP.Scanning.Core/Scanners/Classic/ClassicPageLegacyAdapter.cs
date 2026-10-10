using Microsoft.SharePoint.Client;
using PnP.Core.Model.SharePoint;
using PnP.Core.QueryModel;
using PnP.Core.Services;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Pipeline.Analysis;
using PnP.Scanning.Core.Pipeline.Collection;
using PnP.Scanning.Core.Pipeline.Analysis.WebPartMapping;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Scanners;

/// <summary>The legacy mixed Classic scan shares page acquisition/rules, while retaining its existing storage and lifecycle.</summary>
internal static class ClassicPageLegacyAdapter
{
    internal static readonly WebPartMappingManager MappingManager = new();

    internal static async Task ExecuteAsync(ClassicScanner scanner, PnPContext pnp, ClientContext csom, IReadOnlyList<ClassicPageDiscovery> discovered)
    {
        var token = scanner.ScanManager.GetCancellationTokenSource(scanner.ScanId).Token;
        var options = new Pipeline.Contracts.ClassicPageSourceOptions(scanner.Options.ExportWebPartProperties, scanner.Options.SkipUsageInformation,
            scanner.Options.SkipUserInformation, scanner.Options.HomePageOnly, scanner.Options.AuditLogWindowDays);
        var web = await ClassicPageOnlineSource.ReadWebFactsAsync(pnp, csom, scanner.SiteUrl, scanner.WebUrl, scanner.WebTemplate, token);
        var pages = new List<ClassicPage>(); var parts = new List<ClassicPageWebPart>(); var dispositions = new List<ClassicPageDiscovery>();
        var modern = 0;
        foreach (var row in discovered)
        {
            Pipeline.Contracts.ClassicPageItemSource input;
            var sourceRow = Pipeline.Contracts.ClassicPageSourceJson.Convert<Pipeline.Contracts.ClassicPageDiscoveryRow>(row);
            try { input = await ClassicPageOnlineSource.ReadPageInputsAsync(pnp, csom, web, sourceRow, options.SkipUserInformation, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { input = ClassicPageOnlineSource.EmptyPage(web, sourceRow, ClassicPageOnlineSource.Failure(ex)); }
            var output = ClassicPageProjection.Analyze(scanner.ScanId, web, sourceRow, input, options, ClassicPageLegacyAdapter.MappingManager);
            if (output.Page != null) pages.Add(Pipeline.Contracts.ClassicPageSourceJson.Convert<ClassicPage>(output.Page));
            parts.AddRange(output.Parts.Select(Pipeline.Contracts.ClassicPageSourceJson.Convert<ClassicPageWebPart>));
            dispositions.Add(Pipeline.Contracts.ClassicPageSourceJson.Convert<ClassicPageDiscovery>(output.Discovery));
            if (output.Modern) modern++;
        }
        if (scanner.WebTemplate == "BLOG#0")
        {
            var list = pnp.Web.Lists.AsRequested().FirstOrDefault(x => x.TemplateType == PnP.Core.Model.SharePoint.ListTemplateType.Posts);
            if (list != null)
            {
                string paging = null;
                do
                {
                    var batch = await ClassicPageOnlineSource.LoadBatchAsync(list, ClassicPageQuery.Create(new(), false, null, options.SkipUserInformation), paging, token);
                    foreach (var fields in batch.Items)
                    {
                        var url = fields.GetValueOrDefault("FileRef")?.Text ?? fields.GetValueOrDefault("ID")?.Text ?? "";
                        var home = web.WelcomePageState.Succeeded ? HomePageDetector.IsHomePage(url, web.WelcomePage) : (bool?)null;
                        if (options.HomePageOnly && home != true) continue;
                        pages.Add(new ClassicPage { ScanId = scanner.ScanId, SiteUrl = scanner.SiteUrl, WebUrl = scanner.WebUrl, PageUrl = url,
                            PageName = fields.GetValueOrDefault("Title")?.Text ?? "", ListId = list.Id, ListTitle = list.Title, ListUrl = list.RootFolder.ServerRelativeUrl,
                            ModifiedAt = fields.TryGetValue("Modified", out var modified) && modified.ToValue() is DateTime date ? date : default,
                            ModifiedBy = options.SkipUserInformation ? null : ClassicPageProjection.ModifiedBy(fields),
                            PageType = ClassicPageRules.BlogPage, RemediationCode = "CP4", HomePage = home });
                    }
                    paging = batch.NextPage;
                } while (paging != null);
            }
        }
        await new AssessmentDiscoveryWriter(scanner.ScanId).UpdateExistingAsync(dispositions, token);
        if (pages.Count > 0) await scanner.StorageManager.StorePageInformationAsync(scanner.ScanId, pages);
        if (parts.Count > 0) await scanner.StorageManager.StorePageWebPartsAsync(scanner.ScanId, parts);
        await scanner.StorageManager.StorePageSummaryAsync(scanner.ScanId, scanner.SiteUrl, scanner.WebUrl, scanner.WebTemplate, pnp,
            pages.Select(x => x.RemediationCode).Where(x => !string.IsNullOrEmpty(x)).ToHashSet(), modern,
            pages.Count(x => x.PageType == ClassicPageRules.WikiPage), pages.Count(x => x.PageType == ClassicPageRules.BlogPage),
            pages.Count(x => x.PageType == ClassicPageRules.WebPartPage), pages.Count(x => x.PageType == ClassicPageRules.ASPXPage),
            pages.Count(x => x.PageType == ClassicPageRules.PublishingPage));
    }
}
