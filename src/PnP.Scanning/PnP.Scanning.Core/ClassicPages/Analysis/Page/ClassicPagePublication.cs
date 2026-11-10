using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Analysis.Module;
#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.ClassicPages.Analysis.Audit;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.List;

namespace PnP.Scanning.Core.ClassicPages.Analysis.Page;

/// <summary>Retains the canonical native result before adapting it to existing report persistence.</summary>
internal sealed record ClassicPagePublication(
    ClassicPageRow[] Pages,
    ClassicPageWebPartRow[] Parts,
    DiscoveryEvidence[] Discovery,
    PhysicalFileFacts[] Files,
    WebFacts[] Webs,
    ClassicCollectionScope Scope,
    ClassicComponentSource[] Components,
    ClassicAuditPageStats[] Audit)
{
    /// <summary>
    /// Finalizes canonical native Pages, parts and coverage from fixed source and committed analysis results.
    /// </summary>
    internal static async Task<ClassicPagePublication> BuildAsync(
        Guid assessment,
        ClassicContentIndex index,
        IAnalysisResultReader reader,
        CancellationToken token)
    {
        List<ClassicPageRow> pageCandidates = [];
        List<ClassicPageWebPartRow> parts = [];
        Dictionary<(string, string, string RecordKey), DiscoveryEvidence>? discoveries = index.Evidence
            .Concat(index.Scope.Evidence)
            .ToDictionary(row => (row.SiteUrl ?? "", row.WebUrl ?? "", row.RecordKey));
        List<ClassicAuditPageStats>? audit = new List<ClassicAuditPageStats>();
        foreach (AnalysisResult? result in await reader.ReadAsync(token))
        {
            JsonElement value = result.Payload.Value;
            string? kind = value.GetProperty("kind").GetString();
            if (kind == "FileEnrichment")
            {
                FilePageAnalysis? output = value.GetProperty("output").Deserialize<FilePageAnalysis>()!;
                if (output.Page != null) pageCandidates.Add(output.Page);
                parts.AddRange(output.Parts);
                if (output.Discovery != null)
                {
                    DiscoveryEvidence? row = output.Discovery;
                    discoveries[(row.SiteUrl, row.WebUrl, row.RecordKey)] = row;
                }
            }
            else if (kind == "ListItems")
            {
                foreach (JsonElement page in value.GetProperty("pages").EnumerateArray())
                {
                    if (page.ValueKind == JsonValueKind.Null) continue;
                    ClassicPageRow row = page.Deserialize<ClassicPageRow>()!;
                    pageCandidates.Add(row);
                }
            }
            else if (kind == "AuditPage")
                audit.AddRange(value.GetProperty("output").Deserialize<ClassicAuditPageStats[]>()!);
        }

        foreach (Contracts.Site.SiteCollectionScope? site in index.Scope.SiteCollections)
        {
            foreach (DiscoveryEvidence? evidence in site.Evidence ?? [])
            {
                DiscoveryEvidence? retained = evidence;
                discoveries[(retained.SiteUrl, retained.WebUrl, retained.RecordKey)] = retained;
            }

            DiscoveryEvidence? row = new DiscoveryEvidence
            {
                ScanId = assessment,
                SiteUrl = site.SiteUrl,
                WebUrl = "",
                RecordKey = "assessment:webs:" + DiscoveryHash.Of(site.SiteUrl),
                RowType = DiscoveryRowType.Scope,
                ScopeType = "SiteCollection",
                Url = site.SiteUrl,
                ObservationMethod = "EnumerateWebs",
                DiscoveryStatus = site.State.Status,
                ErrorDetail = site.State.Error,
                ExpectedChildCount = site.State.Succeeded ? site.Webs.Length : null,
                ObservedChildCount = site.State.Succeeded ? site.Webs.Length : null,
                ObservedAtUtc = index.Scope.AuditWindowEnd,
            };
            discoveries[(row.SiteUrl, row.WebUrl, row.RecordKey)] = row;
        }

        foreach (WebFacts? web in index.Webs.Values.Where(x => !x.State.Succeeded))
        {
            DiscoveryEvidence? row = new DiscoveryEvidence
            {
                ScanId = assessment,
                SiteUrl = web.SiteUrl,
                WebUrl = web.WebUrl,
                RecordKey = "assessment:web:" + DiscoveryHash.Of(web.SiteUrl, web.WebUrl),
                RowType = DiscoveryRowType.Scope,
                ScopeType = "Web",
                Url = web.SiteUrl.TrimEnd('/') + web.WebUrl,
                DiscoveryStatus = web.State.Status,
                ErrorDetail = web.State.Error,
                ObservationMethod = "WebMetadata",
                ObservedAtUtc = index.Scope.AuditWindowEnd,
            };
            discoveries[(row.SiteUrl, row.WebUrl, row.RecordKey)] = row;
        }

        AddItemAcquisitionEvidence(assessment, index, discoveries);

        DiscoveryEvidence? coverage = DiscoveryEvidenceRules.BuildCoverage(
            assessment,
            discoveries.Values.ToArray(),
            index.Scope.AuditWindowEnd);
        discoveries[("", "", coverage.RecordKey)] = coverage;
        ClassicPagePublication payload = new(
            PageContentProjection.Build(assessment, index, pageCandidates),
            parts.OrderBy(x => x.SiteUrl)
                .ThenBy(x => x.WebUrl)
                .ThenBy(x => x.PageUrl)
                .ThenBy(x => x.WebPartIndex)
                .ToArray(),
            discoveries.Values.OrderBy(x => x.SiteUrl).ThenBy(x => x.WebUrl).ThenBy(x => x.RecordKey).ToArray(),
            index.Files.Values.OrderBy(x => x.SiteCollectionId).ThenBy(x => x.FileUniqueId).ToArray(),
            index.Webs.Values.OrderBy(x => x.SiteUrl).ThenBy(x => x.WebUrl).ToArray(),
            index.Scope,
            index.Components.Values.OrderBy(x => x.SiteUrl).ThenBy(x => x.WebUrl).ToArray(),
            audit.OrderBy(x => x.PageUrl).ThenBy(x => x.Views).ThenBy(x => x.Edits).ToArray());
        return payload;
    }

    /// <summary>
    /// Preserves failed query, item-page, and native File-link coverage in the existing evidence report without inventing Page rows.
    /// </summary>
    private static void AddItemAcquisitionEvidence(
        Guid assessmentId,
        ClassicContentIndex index,
        Dictionary<(string Site, string Web, string RecordKey), DiscoveryEvidence> evidence)
    {
        /// <summary>Retains one incomplete content-acquisition scope as native gap evidence without creating a Page.</summary>
        void Add(
            string siteUrl,
            string webUrl,
            string recordKey,
            Guid? listId,
            int? itemId,
            SourceReadState state,
            string stage)
        {
            WebFacts web = index.Webs[CollectionJson.WebKey(siteUrl, webUrl)];
            DiscoveryEvidence row = new()
            {
                ScanId = assessmentId,
                SiteUrl = siteUrl,
                WebUrl = webUrl,
                RecordKey = recordKey,
                SiteCollectionId = web.SiteCollectionId == Guid.Empty ? null : web.SiteCollectionId,
                WebId = web.WebId == Guid.Empty ? null : web.WebId,
                ListId = listId,
                ListItemId = itemId,
                RowType = DiscoveryRowType.Gap,
                ScopeType = itemId.HasValue ? "ListItem" : "List",
                DiscoveryStatus =
                    state.Status is "Denied" or "Failed" or "Partial" or "Cancelled" ? state.Status : "Unknown",
                AssessmentStatus = "Unknown",
                ErrorStage = stage,
                ErrorCodes = "content_acquisition_incomplete",
                ErrorDetail = state.Error ?? state.Status,
                ObservationMethod = stage,
                ObservedAtUtc = index.Scope.AuditWindowEnd,
            };
            evidence[(siteUrl, webUrl, recordKey)] = row;
        }

        foreach (WebListItemQueries queries in index.ItemQueries.Values)
            if (!queries.State.Succeeded && !index.Scope.Options.HomePageOnly)
                Add(
                    queries.SiteUrl,
                    queries.WebUrl,
                    "content:query-scope",
                    null,
                    null,
                    queries.State,
                    "ListItemQueries");
        foreach (ListItemBatch batch in index.ItemBatches)
        {
            string key = $"content:{batch.ListId:D}:{batch.PageOrdinal}";
            if (!batch.State.Succeeded)
                Add(batch.SiteUrl, batch.WebUrl, key, batch.ListId, null, batch.State, "ListItemAcquisition");
        }
    }
}