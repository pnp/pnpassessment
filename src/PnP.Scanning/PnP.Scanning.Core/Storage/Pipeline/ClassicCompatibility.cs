#nullable enable
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Pipeline.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Analysis.Audit;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using System.Text.Json.Nodes;
using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Storage.Pipeline;

// This adapter retains the existing database/report contract. It does not define a new CSV shape.
/// <summary>
/// Binds deterministic analysis output to the existing Classic storage and report contracts.
/// </summary>
internal sealed record ClassicCompatibilityPayload(
    ClassicPageRow[] Pages,
    ClassicPageWebPart[] Parts,
    ClassicPageDiscovery[] Discovery,
    SPFile[] Files,
    WebFacts[] Webs,
    ClassicCollectionScope Scope,
    ClassicComponentSource[] Components,
    ClassicAuditPageStats[] Audit);

/// <summary>
/// Builds the narrow existing-report projection from fixed source and committed results.
/// </summary>
internal static class ClassicCompatibility
{
    /// <summary>
    /// Publishes deterministic compatibility data from fixed source and committed analysis results.
    /// </summary>
    internal static async Task PublishAsync(
        Guid assessment,
        ClassicContentIndex index,
        IAnalysisResultReader reader,
        IAnalysisReportWriter writer,
        CancellationToken token)
    {
        List<ClassicPageRow> pageCandidates = [];
        List<ClassicPageWebPart>? parts = new List<ClassicPageWebPart>();
        Dictionary<(string, string, string RecordKey), ClassicPageDiscovery>? discoveries = index.Evidence
            .Select(DiscoveryEvidenceMapper.ToLegacy)
            .Concat(index.Scope.Evidence.Select(DiscoveryEvidenceMapper.ToLegacy))
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
                parts.AddRange(output.Parts.Select(CollectionJson.Convert<ClassicPageWebPart>));
                if (output.Discovery != null)
                {
                    ClassicPageDiscovery? row = DiscoveryEvidenceMapper.ToLegacy(output.Discovery);
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

        foreach (Core.Pipeline.Contracts.Site.SiteCollectionScope? site in index.Scope.SiteCollections)
        {
            foreach (Core.Pipeline.Contracts.Page.DiscoveryEvidence? evidence in site.Evidence ?? [])
            {
                ClassicPageDiscovery? retained = DiscoveryEvidenceMapper.ToLegacy(evidence);
                discoveries[(retained.SiteUrl, retained.WebUrl, retained.RecordKey)] = retained;
            }

            ClassicPageDiscovery? row = new ClassicPageDiscovery
            {
                ScanId = assessment,
                SiteUrl = site.SiteUrl,
                WebUrl = "",
                RecordKey = "assessment:webs:" + DiscoveryHash.Of(site.SiteUrl),
                RowType = "Scope",
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
            ClassicPageDiscovery? row = new ClassicPageDiscovery
            {
                ScanId = assessment,
                SiteUrl = web.SiteUrl,
                WebUrl = web.WebUrl,
                RecordKey = "assessment:web:" + DiscoveryHash.Of(web.SiteUrl, web.WebUrl),
                RowType = "Scope",
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

        ClassicPageDiscovery? coverage = AssessmentDiscoveryWriter.BuildCoverage(
            assessment,
            discoveries.Values.ToArray(),
            index.Scope.AuditWindowEnd);
        discoveries[("", "", coverage.RecordKey)] = coverage;
        ClassicCompatibilityPayload? payload = new ClassicCompatibilityPayload(
            PageContentProjection.Build(assessment, index, pageCandidates),
            parts.OrderBy(x => x.SiteUrl)
                .ThenBy(x => x.WebUrl)
                .ThenBy(x => x.PageUrl)
                .ThenBy(x => x.WebPartIndex)
                .ToArray(),
            discoveries.Values.OrderBy(x => x.SiteUrl).ThenBy(x => x.WebUrl).ThenBy(x => x.RecordKey).ToArray(),
            index.Files.Values.OrderBy(x => x.SiteCollectionId)
                .ThenBy(x => x.FileUniqueId)
                .Select(CollectionJson.Convert<SPFile>)
                .ToArray(),
            index.Webs.Values.OrderBy(x => x.SiteUrl).ThenBy(x => x.WebUrl).ToArray(),
            index.Scope,
            index.Components.Values.OrderBy(x => x.SiteUrl).ThenBy(x => x.WebUrl).ToArray(),
            audit.OrderBy(x => x.PageUrl).ThenBy(x => x.Views).ThenBy(x => x.Edits).ToArray());
        await writer.PublishAsync([new("ClassicCompatibility", "assessment", 0, VersionedJson.From(payload))], token);
    }

    /// <summary>
    /// Preserves failed query, item-page, and native File-link coverage in the existing evidence report without inventing Page rows.
    /// </summary>
    private static void AddItemAcquisitionEvidence(
        Guid assessmentId,
        ClassicContentIndex index,
        Dictionary<(string Site, string Web, string RecordKey), ClassicPageDiscovery> evidence)
    {
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
            ClassicPageDiscovery row = new()
            {
                ScanId = assessmentId,
                SiteUrl = siteUrl,
                WebUrl = webUrl,
                RecordKey = recordKey,
                SiteCollectionId = web.SiteCollectionId == Guid.Empty ? null : web.SiteCollectionId,
                WebId = web.WebId == Guid.Empty ? null : web.WebId,
                ListId = listId,
                ListItemId = itemId,
                RowType = "Gap",
                ScopeType = itemId.HasValue ? "ListItem" : "List",
                DiscoveryStatus = state.Status,
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
            if (batch.QueryKind != ListItemQueryKind.PointPublishingFiles) continue;
            foreach (ListItemFacts item in
                     batch.Items.Where(item => item.FileAssociation is not { State.Succeeded: true }))
                Add(
                    batch.SiteUrl,
                    batch.WebUrl,
                    key + ":" + item.ListItemId,
                    batch.ListId,
                    item.ListItemId,
                    item.FileAssociation?.State ?? new(
                        "Unknown",
                        "Native File relationship evidence was not acquired."),
                    "NativeFileAssociation");
        }
    }

    /// <summary>
    /// Adapts optional backing-list metadata to the historical storage shape only; the required legacy sentinel never becomes native Page identity.
    /// </summary>
    internal static ClassicPage ToLegacyPage(ClassicPageRow page)
    {
        JsonObject values = JsonSerializer.SerializeToNode(page)?.AsObject() ??
                            throw new SnapshotIntegrityException(
                                "Page compatibility serialization returned no object.");
        values[nameof(ClassicPage.ListId)] = JsonSerializer.SerializeToNode(page.ListId ?? Guid.Empty);
        return values.Deserialize<ClassicPage>() ??
               throw new SnapshotIntegrityException("Page compatibility serialization failed.");
    }

    /// <summary>
    /// Rejects unrepresentable or colliding legacy URL keys before writes; canonical native objects remain in immutable analysis publication.
    /// </summary>
    internal static ClassicPage[] LegacyPages(ClassicPageRow[] pages)
    {
        Dictionary<(string Site, string Web, string Url), PageIdentity> legacyKeys = [];
        List<ClassicPage> legacy = [];
        foreach (ClassicPageRow page in pages.Where(page => page.PageType != ClassicPageRules.ModernPage))
        {
            PageIdentity identity = page.Identity?.Validated() ??
                                    throw new SnapshotIntegrityException(
                                        "Compatibility output requires native Page identity.");
            if (string.IsNullOrWhiteSpace(page.PageUrl))
                throw new SnapshotIntegrityException(
                    $"The existing report contract cannot represent content {identity.Key} without an acquired locator; native evidence is retained.");
            (string Site, string Web, string Url) key = (page.SiteUrl.ToUpperInvariant(),
                page.WebUrl.ToUpperInvariant(), page.PageUrl.ToUpperInvariant());
            if (legacyKeys.TryGetValue(key, out PageIdentity? previous) && previous != identity)
                throw new SnapshotIntegrityException(
                    $"Distinct native content {previous.Key} and {identity.Key} share one legacy URL key. The existing report contract cannot represent both; no object was discarded.");
            legacyKeys.Add(key, identity);
            legacy.Add(ToLegacyPage(page));
        }

        return legacy.ToArray();
    }

    /// <summary>
    /// Builds existing Web totals while retaining captured non-page component results.
    /// </summary>
    internal static ClassicWebSummary BuildWeb(
        Guid assessment,
        WebFacts source,
        ClassicPage[] pages,
        ClassicPageDiscovery[] discovery,
        ClassicWebSummary? other)
    {
        ClassicWebSummary? row = other ?? new ClassicWebSummary();
        row.ScanId = assessment;
        row.SiteUrl = source.SiteUrl;
        row.WebUrl = source.WebUrl;
        row.Template = source.WebTemplate;
        row.LastItemUserModifiedDate = source.LastItemUserModifiedDate;
        ClassicPage[]? selected = pages.Where(x => x.SiteUrl == source.SiteUrl && x.WebUrl == source.WebUrl).ToArray();
        row.ClassicPages = selected.Length;
        row.ClassicWikiPages = selected.Count(x => x.PageType == ClassicPageRules.WikiPage);
        row.ClassicASPXPages = selected.Count(x => x.PageType == ClassicPageRules.ASPXPage);
        row.ClassicBlogPages =
            selected.Count(x => x.PageType is ClassicPageRules.BlogPage or ClassicPageRules.DelveBlogPage);
        row.ClassicWebPartPages = selected.Count(x => x.PageType == ClassicPageRules.WebPartPage);
        row.ClassicPublishingPages = selected.Count(x => x.PageType == ClassicPageRules.PublishingPage);
        row.ModernPages = discovery.Count(x => x.SiteUrl == source.SiteUrl && x.WebUrl == source.WebUrl &&
                                               x.RowType == "Page" && x.PageType == ClassicPageRules.ModernPage);
        SiteType type = ClassicScanner.GetSiteType(source.WebTemplate);
        row.IsClassicPublishingSite = type == SiteType.Publishing;
        row.IsModernSite = type == SiteType.Modern;
        row.IsModernCommunicationSite = type == SiteType.Communication;
        row.RemediationCode = type == SiteType.Publishing
            ? "CS2"
            : type == SiteType.Blog
                ? "CS1"
                : null;
        row.AggregatedRemediationCodes = string.Join(
            ',',
            selected.Select(x => x.RemediationCode)
                .Append(row.RemediationCode)
                .Concat((row.AggregatedRemediationCodes ?? "").Split(','))
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct()
                .OrderBy(x => x, StringComparer.Ordinal));
        return row;
    }

    /// <summary>
    /// Aggregates existing Web summaries into the established per-site counts and weighted readiness totals.
    /// </summary>
    internal static ClassicSiteSummary BuildSite(Guid assessment, IGrouping<string, ClassicWebSummary> group)
    {
        ClassicWebSummary? root = group.FirstOrDefault(x => x.WebUrl == "/");
        ClassicWebSummary[]? sub = group.Where(x => x.WebUrl != "/").ToArray();
        int weighted = group.Sum(x => x.PagesWithWebParts);
        return new()
        {
            ScanId = assessment,
            SiteUrl = group.Key,
            RootWebTemplate = root?.Template,
            SubWebTemplates = string.Join(',', sub.Select(x => x.Template).Distinct()),
            SubWebCount = sub.Length,
            SubWebDepth = sub.Length == 0 ? 0 : sub.Max(x => x.WebUrl.Count(c => c == '/')),
            LastItemUserModifiedDate = group.Max(x => x.LastItemUserModifiedDate),
            ClassicLists = group.Sum(x => x.ClassicLists),
            ModernLists = group.Sum(x => x.ModernLists),
            ClassicPages = group.Sum(x => x.ClassicPages),
            ModernPages = group.Sum(x => x.ModernPages),
            ClassicWikiPages = group.Sum(x => x.ClassicWikiPages),
            ClassicASPXPages = group.Sum(x => x.ClassicASPXPages),
            ClassicBlogPages = group.Sum(x => x.ClassicBlogPages),
            ClassicWebPartPages = group.Sum(x => x.ClassicWebPartPages),
            ClassicPublishingPages = group.Sum(x => x.ClassicPublishingPages),
            PagesWithWebParts = weighted,
            MappableWebPartPages = group.Sum(x => x.MappableWebPartPages),
            UnmappedWebPartPages = group.Sum(x => x.UnmappedWebPartPages),
            UncustomizedHomePages = group.Sum(x => x.UncustomizedHomePages),
            AvgMappingPercentage =
                weighted == 0 ? 0 : group.Sum(x => x.AvgMappingPercentage * x.PagesWithWebParts) / weighted,
            ClassicWorkflows = group.Sum(x => x.ClassicWorkflows),
            ClassicInfoPathForms = group.Sum(x => x.ClassicInfoPathForms),
            ClassicExtensibilities = group.Sum(x => x.ClassicExtensibilities),
            SharePointAddIns = group.Sum(x => x.SharePointAddIns),
            AzureACSPrincipals = group.Sum(x => x.AzureACSPrincipals),
            AggregatedRemediationCodes = string.Join(
                ',',
                group.SelectMany(x => (x.AggregatedRemediationCodes ?? "").Split(','))
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Distinct()
                    .OrderBy(x => x, StringComparer.Ordinal)),
        };
    }
}

/// <summary>
/// Stores assessment-scoped immutable evidence, phase state, and bound analysis results transactionally.
/// </summary>
internal sealed partial class PipelineStore
{
    /// <summary>
    /// Atomically validates and materializes immutable compatibility output into existing assessment report tables.
    /// </summary>
    internal Task<bool> MaterializeClassicAsync(Guid assessment, Guid runId, CancellationToken token) =>
        WriteAsync(
            assessment,
            async db =>
            {
                PinnedManifest? manifest = await AnalysisManifestAsync(db, runId, token);
                PhaseRunRow? phase = await db.PhaseRuns.SingleAsync(x => x.RunId == runId, token);
                if (phase.Status != ScanStatus.Finished || phase.ModuleKey != "classicpage")
                    throw new InvalidOperationException("Classic reporting requires a completed analysis.");
                AnalysisReportRow? report =
                    (await ReadReportsAsync(db, runId, token)).Single(x => x.Kind == "ClassicCompatibility");
                ClassicCompatibilityPayload? payload =
                    report.Payload.Value.Deserialize<ClassicCompatibilityPayload>() ??
                    throw new SnapshotIntegrityException("Classic compatibility publication is missing.");
                ClassicPage[] legacyPages = ClassicCompatibility.LegacyPages(payload.Pages);

                async Task Replace<T>(IEnumerable<T> rows) where T : class
                {
                    await db.Set<T>()
                        .Where(x => EF.Property<Guid>(x, "ScanId") == assessment)
                        .ExecuteDeleteAsync(token);
                    db.Set<T>().AddRange(rows);
                }

                foreach (ClassicComponentSource? component in payload.Components)
                foreach (KeyValuePair<string, JsonElement[]> rows in component.Rows)
                    if (rows.Value.Any(value => value.GetProperty("ScanId").GetGuid() != assessment ||
                                                value.GetProperty("SiteUrl").GetString() != component.SiteUrl ||
                                                value.GetProperty("WebUrl").GetString() != component.WebUrl))
                        throw new SnapshotIntegrityException("Classic component rows have foreign ownership.");

                T[] Components<T>() =>
                    payload.Components.SelectMany(x => x.Rows.GetValueOrDefault(typeof(T).Name) ?? [])
                        .Select(value => value.Deserialize<T>()!)
                        .ToArray();

                await Replace(Components<ClassicList>());
                await Replace(Components<ClassicInfoPath>());
                await Replace(Components<Workflow>());
                await Replace(Components<ClassicExtensibility>());
                await Replace(Components<ClassicUserCustomAction>());
                await Replace(legacyPages);
                await Replace(payload.Parts);
                await Replace(payload.Discovery);
                await Replace(payload.Files);
                Dictionary<(string SiteUrl, string WebUrl), ClassicWebSummary>? existing =
                    Components<ClassicWebSummary>().ToDictionary(x => (x.SiteUrl, x.WebUrl));
                await Replace(
                    payload.Webs.Select(web => ClassicCompatibility.BuildWeb(
                        assessment,
                        web,
                        legacyPages,
                        payload.Discovery,
                        existing.GetValueOrDefault((web.SiteUrl, web.WebUrl)))));
                await Replace(
                    payload.Scope.SiteCollections.Select(site => new SiteCollection
                    {
                        ScanId = assessment,
                        SiteUrl = site.SiteUrl,
                        Status = site.State.Succeeded ? SiteWebStatus.Finished : SiteWebStatus.Failed,
                        Error = site.State.Error,
                    }));
                await Replace(
                    payload.Webs.Select(web => new Web
                    {
                        ScanId = assessment,
                        SiteUrl = web.SiteUrl,
                        WebUrl = web.WebUrl,
                        WebUrlAbsolute = web.SiteUrl.TrimEnd('/') + (web.WebUrl == "/" ? "" : web.WebUrl),
                        Template = web.WebTemplate,
                        Status = web.State.Succeeded ? SiteWebStatus.Finished : SiteWebStatus.Failed,
                        Error = web.State.Error,
                    }));
                await db.SaveChangesAsync(token);
                await db.ClassicWebPartUniques.Where(x => x.ScanId == assessment).ExecuteDeleteAsync(token);
                await db.ClassicPublishingSiteSummaries.Where(x => x.ScanId == assessment).ExecuteDeleteAsync(token);
                await db.ClassicSiteSummaries.Where(x => x.ScanId == assessment).ExecuteDeleteAsync(token);
                await db.ClassicPageAuditUsages.Where(x => x.ScanId == assessment).ExecuteDeleteAsync(token);
                if (!payload.Scope.Options.SkipUsageInformation)
                {
                    List<Core.Pipeline.Contracts.Audit.ClassicPageAuditChunkSource>? chunks =
                        new List<PnP.Scanning.Core.Pipeline.Contracts.Audit.ClassicPageAuditChunkSource>();
                    foreach (ManifestMember? member in manifest.Members.Values)
                    {
                        SourceRecord? source = await ReadSelectedObservationAsync(
                            db,
                            await db.SourceSnapshots.SingleAsync(x => x.SnapshotId == phase.SnapshotId, token),
                            member.ObservationId,
                            manifest,
                            token);
                        if (CollectionJson.Kind(source) == "AuditChunk")
                            chunks.Add(
                                CollectionJson
                                    .Read<PnP.Scanning.Core.Pipeline.Contracts.Audit.ClassicPageAuditChunkSource>(
                                        source.Artifact.GetBytes()));
                    }

                    int succeeded = chunks.Count(x => x.Status == "succeeded");
                    string? status = succeeded == chunks.Count && succeeded > 0
                        ? "succeeded"
                        : succeeded > 0
                            ? "partial"
                            : chunks.All(x => x.Status == "skipped")
                                ? "skipped"
                                : "failed";
                    IGrouping<string, ClassicAuditPageStats>[]? merged =
                        payload.Audit.GroupBy(x => x.PageUrl, StringComparer.OrdinalIgnoreCase).ToArray();
                    foreach (string? site in payload.Scope.SiteCollectionUrls)
                    {
                        IGrouping<string, ClassicAuditPageStats>[]? stats = merged.Where(group =>
                                payload.Scope.SiteCollectionUrls.OrderByDescending(x => x.Length)
                                    .FirstOrDefault(candidate => group.Key.StartsWith(
                                        candidate.TrimEnd('/') + "/",
                                        StringComparison.OrdinalIgnoreCase)) == site)
                            .ToArray();

                        ClassicPageAuditUsage Row(string url) =>
                            new()
                            {
                                ScanId = assessment,
                                SiteUrl = site,
                                WebUrl = "/",
                                PageUrl = new Uri(url).AbsolutePath,
                                AuditWindowStart = payload.Scope.AuditWindowStart,
                                AuditWindowEnd = payload.Scope.AuditWindowEnd,
                                QueryStatus = status,
                                SkipReason = string.Join(
                                    "; ",
                                    chunks.Where(x => x.Error != null).Select(x => x.Error)),
                            };

                        foreach (IGrouping<string, ClassicAuditPageStats>? group in stats)
                        {
                            ClassicPageAuditUsage? row = Row(group.Key);
                            row.AuditViewsCount = group.Sum(x => x.Views);
                            row.AuditCreatesCount = group.Sum(x => x.Creates);
                            row.AuditEditsCount = group.Sum(x => x.Edits);
                            row.AuditUniqueUsers = group.SelectMany(x => x.UserHashes).Distinct().Take(10000).Count();
                            db.ClassicPageAuditUsages.Add(row);
                        }

                        if (stats.Length == 0 || status == "partial") db.ClassicPageAuditUsages.Add(Row(site));
                    }
                }

                await StorageManager.ComputeAndStoreWebPageRollupsAsync(db, assessment);
                await StorageManager.PopulateWebPartUniqueAsync(db, assessment, PageScanComponent.MappingManager);
                await StorageManager.PopulatePublishingSiteSummaryAsync(db, assessment);
                List<ClassicWebSummary>? summaries =
                    await db.ClassicWebSummaries.Where(x => x.ScanId == assessment).ToListAsync(token);
                db.ClassicSiteSummaries.AddRange(
                    summaries.GroupBy(x => x.SiteUrl)
                        .Select(group => ClassicCompatibility.BuildSite(assessment, group)));
                Scan? scan = await db.Scans.SingleAsync(x => x.ScanId == assessment, token);
                scan.CLIMode = Services.Mode.Classic.ToString();
                scan.PreScanStatus = scan.PostScanStatus = SiteWebStatus.Finished;
                return true;
            },
            token);
}