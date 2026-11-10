using PnP.Scanning.Core.ClassicPages.Collection.Module;
using PnP.Scanning.Core.ClassicPages.Compatibility;
using PnP.Scanning.Core.ClassicPages.Collection.Discovery;
using PnP.Scanning.Core.Discovery;
#nullable enable
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Core.Auth;
using PnP.Core.Services;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Collection.Page;
using PnP.Scanning.Core.Pipeline.Collection.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;


namespace PnP.Scanning.Core.ClassicPages.Collection.File;

/// <summary>Collects native physical inventory, discovery receipts and original ASPX evidence for one Web.</summary>
internal sealed class FileInventoryReader(
    CollectionContext context,
    CollectionServices services,
    ExternalAuthenticationProvider auth,
    PublishingLayoutReader layoutReader)
{
    /// <summary>
    /// Acquires journaled physical-file and ASPX discovery evidence under the selected native Web scope.
    /// </summary>
    internal async Task<WebFileInventory> ReadAsync(WebFacts web, AcquisitionJournal journal, CancellationToken token)
    {
        Dictionary<string, DiscoveryEvidence>? rows = new Dictionary<string, DiscoveryEvidence>(StringComparer.Ordinal);
        Dictionary<(Guid Site, Guid File), PhysicalFileFacts>? files =
            new Dictionary<(Guid Site, Guid File), PhysicalFileFacts>();
        AssessmentDiscoveryWriter? writer = new AssessmentDiscoveryWriter(
            (batch, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                foreach (DiscoveryEvidence? row in batch)
                {
                    if (rows.TryGetValue(row.RecordKey, out DiscoveryEvidence? previous))
                        DiscoveryEvidenceRules.Reconcile(previous, row);
                    rows[row.RecordKey] = row;
                }

                return Task.CompletedTask;
            },
            (batch, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                foreach (PhysicalFileFacts? file in batch)
                {
                    if (file.SiteCollectionId != web.SiteCollectionId || file.WebId.HasValue && file.WebId != web.WebId)
                        throw new SnapshotIntegrityException(
                            "A physical file observation belongs to a foreign Web scope.");
                    if (file.SiteUrl != web.SiteUrl || file.WebUrl != web.WebUrl)
                    {
                        System.Text.Json.JsonElement[]? evidence =
                            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(
                                file.ProvenanceJson) ?? throw new SnapshotIntegrityException(
                                "Physical file provenance must be an evidence array.");
                        file.ProvenanceJson = System.Text.Json.JsonSerializer.Serialize(
                            evidence.Cast<object>()
                                .Append(
                                    new
                                    {
                                        source = "SnapshotWebScope",
                                        rawSiteUrl = file.SiteUrl,
                                        rawWebUrl = file.WebUrl
                                    }));
                        file.SiteUrl = web.SiteUrl;
                        file.WebUrl = web.WebUrl;
                    }

                    (Guid SiteCollectionId, Guid FileUniqueId) key = (file.SiteCollectionId, file.FileUniqueId);
                    files[key] = PhysicalFileConsolidator.Consolidate(files.GetValueOrDefault(key), file);
                    layoutReader.Seed(web.SiteCollectionId, file.ServerRelativeUrl, file.CustomizedPageStatus);
                }

                return Task.CompletedTask;
            });
        Uri? uri = new Uri(web.SiteUrl.TrimEnd('/') + (web.WebUrl == "/" ? "" : web.WebUrl));
        using ClassicPageDiscoveryJournal? factory = new ClassicPageDiscoveryJournal(
            new PnPContextSharePointAspxRestClientFactory(services.ContextFactory, auth),
            journal);
        using SharePointLiveAspxDiscoveryProvider? provider = new SharePointLiveAspxDiscoveryProvider(
            new(
                "assessment:" + context.AssessmentId,
                "scheduled-web",
                "classic-assessment",
                DiscoveryHash.Of(
                    "classic-assessment",
                    context.AssessmentId.ToString("D"),
                    web.SiteCollectionId.ToString("D"),
                    web.WebId.ToString("D")),
                Intent: ClassicPageModule.Options(context.Options).HomePageOnly
                    ? AspxDiscoveryIntent.HomePageOnly
                    : AspxDiscoveryIntent.FullInventory),
            factory,
            new(web.SiteCollectionId, new Uri(web.SiteUrl), web.WebId, uri, uri.AbsolutePath, web.WebTemplate));
        await new AssessmentWebDiscovery(context.AssessmentId, web.SiteUrl, web.WebUrl, writer).RunAsync(
            provider,
            token);
        try
        {
            using PnPContext? pnp = await services.ContextFactory.CreateAsync(
                uri,
                auth,
                token,
                new PnPContextOptions
                {
                    Properties = new Dictionary<string, object> { ["PipelineAssessmentId"] = context.AssessmentId }
                });
            using ClassicContentReader.CsomLease? csom = ClassicContentReader.Csom(pnp, services, token);
            AspxPageEnricher? enricher = new AspxPageEnricher(pnp, csom.Context, new Uri(web.SiteUrl), uri);
            if (!ClassicPageModule.Options(context.Options).HomePageOnly) await enricher.LoadRolesAsync(token);
            foreach (PhysicalFileFacts? file in
                     files.Values.Where(f => SharePointAspxUrlPolicy.IsAspxPath(f.ServerRelativeUrl)))
                file.AspxEvidenceJson = await journal.ReadOrAcquireAsync(
                    "AspxSource",
                    file.SiteCollectionId.ToString("D") + "|" + file.FileUniqueId.ToString("D"),
                    () => enricher.ReadAsync(file, token),
                    token,
                    json => System.Text.Json.JsonSerializer.Deserialize<AspxPageEvidence>(json)!.SourceReadState ==
                            "Available"
                        ? AcquisitionStatus.Complete
                        : AcquisitionStatus.Partial);
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            Serilog.Log.Warning(error, "ASPX enrichment context failed for {WebUrl}; retaining discovered files", uri);
            foreach (PhysicalFileFacts? file in files.Values.Where(f =>
                         SharePointAspxUrlPolicy.IsAspxPath(f.ServerRelativeUrl) && f.AspxEvidenceJson == null))
                file.AspxEvidenceJson = System.Text.Json.JsonSerializer.Serialize(
                    new AspxPageEvidence(
                        "Failed",
                        null,
                        null,
                        "Unresolved",
                        null,
                        AssessmentWebDiscovery.ErrorDetail(error),
                        null,
                        null,
                        null,
                        null,
                        "Failed",
                        DateTime.UtcNow));
        }

        token.ThrowIfCancellationRequested();
        return new(
            web.SiteUrl,
            web.WebUrl,
            rows.Values.OrderBy(x => x.RecordKey, StringComparer.Ordinal).ToArray(),
            files.Values.OrderBy(x => x.SiteCollectionId).ThenBy(x => x.FileUniqueId).ToArray());
    }
}