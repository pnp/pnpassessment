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
using PnP.Scanning.Core.Scanners;

namespace PnP.Scanning.Core.Storage.Pipeline;

// This adapter retains the existing database/report contract. It does not define a new CSV shape.
internal sealed record ClassicCompatibilityPayload(
    ClassicPage[] Pages, ClassicPageWebPart[] Parts, ClassicPageDiscovery[] Discovery, SPFile[] Files,
    ClassicPageWebSource[] Webs, ClassicPageScopeSource Scope, ClassicComponentSource[] Components,
    ClassicAuditPageStats[] Audit);

internal static class ClassicCompatibility
{
    internal static async Task PublishAsync(Guid assessment, ClassicPageInputIndex index,
        IAnalysisResultReader reader, IAnalysisReportWriter writer, CancellationToken token)
    {
        var pages = new List<ClassicPage>();
        var parts = new List<ClassicPageWebPart>();
        var discoveries = index.Discovery.Select(ClassicPageSourceJson.Convert<ClassicPageDiscovery>)
            .Concat(index.Scope.Evidence.Select(ClassicPageSourceJson.Convert<ClassicPageDiscovery>))
            .ToDictionary(row => (row.SiteUrl ?? "", row.WebUrl ?? "", row.RecordKey));
        var audit = new List<ClassicAuditPageStats>();
        foreach (var result in await reader.ReadAsync(token))
        {
            var value = result.Payload.Value;
            var kind = value.GetProperty("kind").GetString();
            if (kind == "Page")
            {
                var output = value.GetProperty("output").Deserialize<ClassicPageProjectionResult>()!;
                if (output.Page != null) pages.Add(ClassicPageSourceJson.Convert<ClassicPage>(output.Page));
                parts.AddRange(output.Parts.Select(ClassicPageSourceJson.Convert<ClassicPageWebPart>));
                if (output.Discovery != null)
                {
                    var row = ClassicPageSourceJson.Convert<ClassicPageDiscovery>(output.Discovery);
                    discoveries[(row.SiteUrl, row.WebUrl, row.RecordKey)] = row;
                }
                foreach (var site in index.Scope.SiteScopes)
                {
                    foreach (var evidence in site.Evidence ?? [])
                    {
                        var retained = ClassicPageSourceJson.Convert<ClassicPageDiscovery>(evidence);
                        discoveries[(retained.SiteUrl, retained.WebUrl, retained.RecordKey)] = retained;
                    }
                    var row = new ClassicPageDiscovery
                    {
                        ScanId = assessment, SiteUrl = site.SiteUrl, WebUrl = "", RecordKey = "assessment:webs:" + DiscoveryHash.Of(site.SiteUrl),
                        RowType = "Scope", ScopeType = "SiteCollection", Url = site.SiteUrl, ObservationMethod = "EnumerateWebs",
                        DiscoveryStatus = site.State.Status, ErrorDetail = site.State.Error,
                        ExpectedChildCount = site.State.Succeeded ? site.WebUrls.Length : null,
                        ObservedChildCount = site.State.Succeeded ? site.WebUrls.Length : null,
                        ObservedAtUtc = index.Scope.AuditWindowEnd,
                    };
                    discoveries[(row.SiteUrl, row.WebUrl, row.RecordKey)] = row;
                }
                foreach (var web in index.Webs.Values.Where(x => !x.State.Succeeded))
                {
                    var row = new ClassicPageDiscovery
                    {
                        ScanId = assessment, SiteUrl = web.SiteUrl, WebUrl = web.WebUrl,
                        RecordKey = "assessment:web:" + DiscoveryHash.Of(web.SiteUrl, web.WebUrl), RowType = "Scope", ScopeType = "Web",
                        Url = web.SiteUrl.TrimEnd('/') + web.WebUrl, DiscoveryStatus = web.State.Status,
                        ErrorDetail = web.State.Error, ObservationMethod = "WebMetadata", ObservedAtUtc = index.Scope.AuditWindowEnd,
                    };
                    discoveries[(row.SiteUrl, row.WebUrl, row.RecordKey)] = row;
                }
                var coverage = AssessmentDiscoveryWriter.BuildCoverage(assessment, discoveries.Values.ToArray(), index.Scope.AuditWindowEnd);
                discoveries[("", "", coverage.RecordKey)] = coverage;
            }
            else if (kind == "Blog")
            {
                foreach (var page in value.GetProperty("pages").EnumerateArray())
                {
                    if (page.ValueKind == JsonValueKind.Null) continue;
                    var row = page.Deserialize<ClassicPage>()!;
                    // Unknown Blog locators remain in immutable results, not invalid legacy primary keys.
                    if (!string.IsNullOrEmpty(row.PageUrl)) pages.Add(row);
                }
            }
            else if (kind == "AuditPage")
                audit.AddRange(value.GetProperty("output").Deserialize<ClassicAuditPageStats[]>()!);
        }
        var payload = new ClassicCompatibilityPayload(
            pages.OrderBy(x => x.SiteUrl).ThenBy(x => x.WebUrl).ThenBy(x => x.PageUrl).ToArray(),
            parts.OrderBy(x => x.SiteUrl).ThenBy(x => x.WebUrl).ThenBy(x => x.PageUrl).ThenBy(x => x.WebPartIndex).ToArray(),
            discoveries.Values.OrderBy(x => x.SiteUrl).ThenBy(x => x.WebUrl).ThenBy(x => x.RecordKey).ToArray(),
            index.Files.Values.OrderBy(x => x.SiteCollectionId).ThenBy(x => x.FileUniqueId)
                .Select(ClassicPageSourceJson.Convert<SPFile>).ToArray(),
            index.Webs.Values.OrderBy(x => x.SiteUrl).ThenBy(x => x.WebUrl).ToArray(), index.Scope,
            index.Components.Values.OrderBy(x => x.SiteUrl).ThenBy(x => x.WebUrl).ToArray(),
            audit.OrderBy(x => x.PageUrl).ThenBy(x => x.Views).ThenBy(x => x.Edits).ToArray());
        await writer.PublishAsync([new("ClassicCompatibility", "assessment", 0, VersionedJson.From(payload))], token);
    }

    internal static ClassicWebSummary BuildWeb(Guid assessment, ClassicPageWebSource source,
        ClassicPage[] pages, ClassicPageDiscovery[] discovery, ClassicWebSummary? other)
    {
        var row = other ?? new ClassicWebSummary();
        row.ScanId = assessment; row.SiteUrl = source.SiteUrl; row.WebUrl = source.WebUrl;
        row.Template = source.Template; row.LastItemUserModifiedDate = source.LastItemUserModifiedDate;
        var selected = pages.Where(x => x.SiteUrl == source.SiteUrl && x.WebUrl == source.WebUrl).ToArray();
        row.ClassicPages = selected.Length;
        row.ClassicWikiPages = selected.Count(x => x.PageType == ClassicPageRules.WikiPage);
        row.ClassicASPXPages = selected.Count(x => x.PageType == ClassicPageRules.ASPXPage);
        row.ClassicBlogPages = selected.Count(x => x.PageType is ClassicPageRules.BlogPage or ClassicPageRules.DelveBlogPage);
        row.ClassicWebPartPages = selected.Count(x => x.PageType == ClassicPageRules.WebPartPage);
        row.ClassicPublishingPages = selected.Count(x => x.PageType == ClassicPageRules.PublishingPage);
        row.ModernPages = discovery.Count(x => x.SiteUrl == source.SiteUrl && x.WebUrl == source.WebUrl &&
            x.RowType == "Page" && x.PageType == ClassicPageRules.ModernPage);
        var type = ClassicScanner.GetSiteType(source.Template);
        row.IsClassicPublishingSite = type == SiteType.Publishing;
        row.IsModernSite = type == SiteType.Modern;
        row.IsModernCommunicationSite = type == SiteType.Communication;
        row.RemediationCode = type == SiteType.Publishing ? "CS2" : type == SiteType.Blog ? "CS1" : null;
        row.AggregatedRemediationCodes = string.Join(',', selected.Select(x => x.RemediationCode)
            .Append(row.RemediationCode).Concat((row.AggregatedRemediationCodes ?? "").Split(','))
            .Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        return row;
    }

    internal static ClassicSiteSummary BuildSite(Guid assessment, IGrouping<string, ClassicWebSummary> group)
    {
        var root = group.FirstOrDefault(x => x.WebUrl == "/");
        var sub = group.Where(x => x.WebUrl != "/").ToArray();
        var weighted = group.Sum(x => x.PagesWithWebParts);
        return new()
        {
            ScanId = assessment, SiteUrl = group.Key, RootWebTemplate = root?.Template,
            SubWebTemplates = string.Join(',', sub.Select(x => x.Template).Distinct()),
            SubWebCount = sub.Length, SubWebDepth = sub.Length == 0 ? 0 : sub.Max(x => x.WebUrl.Count(c => c == '/')),
            LastItemUserModifiedDate = group.Max(x => x.LastItemUserModifiedDate),
            ClassicLists = group.Sum(x => x.ClassicLists), ModernLists = group.Sum(x => x.ModernLists),
            ClassicPages = group.Sum(x => x.ClassicPages), ModernPages = group.Sum(x => x.ModernPages),
            ClassicWikiPages = group.Sum(x => x.ClassicWikiPages), ClassicASPXPages = group.Sum(x => x.ClassicASPXPages),
            ClassicBlogPages = group.Sum(x => x.ClassicBlogPages), ClassicWebPartPages = group.Sum(x => x.ClassicWebPartPages),
            ClassicPublishingPages = group.Sum(x => x.ClassicPublishingPages), PagesWithWebParts = weighted,
            MappableWebPartPages = group.Sum(x => x.MappableWebPartPages),
            UnmappedWebPartPages = group.Sum(x => x.UnmappedWebPartPages), UncustomizedHomePages = group.Sum(x => x.UncustomizedHomePages),
            AvgMappingPercentage = weighted == 0 ? 0 : group.Sum(x => x.AvgMappingPercentage * x.PagesWithWebParts) / weighted,
            ClassicWorkflows = group.Sum(x => x.ClassicWorkflows), ClassicInfoPathForms = group.Sum(x => x.ClassicInfoPathForms),
            ClassicExtensibilities = group.Sum(x => x.ClassicExtensibilities), SharePointAddIns = group.Sum(x => x.SharePointAddIns),
            AzureACSPrincipals = group.Sum(x => x.AzureACSPrincipals),
            AggregatedRemediationCodes = string.Join(',', group.SelectMany(x => (x.AggregatedRemediationCodes ?? "").Split(','))
                .Where(x => !string.IsNullOrEmpty(x)).Distinct().OrderBy(x => x, StringComparer.Ordinal)),
        };
    }
}

internal sealed partial class PipelineStore
{
    internal Task<bool> MaterializeClassicAsync(Guid assessment, Guid runId, CancellationToken token) =>
        WriteAsync(assessment, async db =>
        {
            var manifest = await AnalysisManifestAsync(db, runId, token);
            var phase = await db.PhaseRuns.SingleAsync(x => x.RunId == runId, token);
            if (phase.Status != ScanStatus.Finished || phase.ModuleKey != "classicpage")
                throw new InvalidOperationException("Classic reporting requires a completed analysis.");
            var report = (await ReadReportsAsync(db, runId, token)).Single(x => x.Kind == "ClassicCompatibility");
            var payload = report.Payload.Value.Deserialize<ClassicCompatibilityPayload>()
                ?? throw new SnapshotIntegrityException("Classic compatibility publication is missing.");
            async Task Replace<T>(IEnumerable<T> rows) where T : class
            {
                await db.Set<T>().Where(x => EF.Property<Guid>(x, "ScanId") == assessment).ExecuteDeleteAsync(token);
                db.Set<T>().AddRange(rows);
            }
            foreach (var component in payload.Components)
                foreach (var rows in component.Rows)
                    if (rows.Value.Any(value => value.GetProperty("ScanId").GetGuid() != assessment ||
                        value.GetProperty("SiteUrl").GetString() != component.SiteUrl ||
                        value.GetProperty("WebUrl").GetString() != component.WebUrl))
                        throw new SnapshotIntegrityException("Classic component rows have foreign ownership.");
            T[] Components<T>() => payload.Components.SelectMany(x => x.Rows.GetValueOrDefault(typeof(T).Name) ?? [])
                .Select(value => value.Deserialize<T>()!).ToArray();
            await Replace(Components<ClassicList>());
            await Replace(Components<ClassicInfoPath>());
            await Replace(Components<Workflow>());
            await Replace(Components<ClassicExtensibility>());
            await Replace(Components<ClassicUserCustomAction>());
            await Replace(payload.Pages);
            await Replace(payload.Parts);
            await Replace(payload.Discovery);
            await Replace(payload.Files);
            var existing = Components<ClassicWebSummary>().ToDictionary(x => (x.SiteUrl, x.WebUrl));
            await Replace(payload.Webs.Select(web => ClassicCompatibility.BuildWeb(assessment, web, payload.Pages,
                payload.Discovery, existing.GetValueOrDefault((web.SiteUrl, web.WebUrl)))));
            await Replace(payload.Scope.SiteScopes.Select(site => new SiteCollection
            {
                ScanId = assessment, SiteUrl = site.SiteUrl, Status = site.State.Succeeded ? SiteWebStatus.Finished : SiteWebStatus.Failed,
                Error = site.State.Error,
            }));
            await Replace(payload.Webs.Select(web => new Web
            {
                ScanId = assessment, SiteUrl = web.SiteUrl, WebUrl = web.WebUrl,
                WebUrlAbsolute = web.SiteUrl.TrimEnd('/') + (web.WebUrl == "/" ? "" : web.WebUrl),
                Template = web.Template, Status = web.State.Succeeded ? SiteWebStatus.Finished : SiteWebStatus.Failed, Error = web.State.Error,
            }));
            await db.SaveChangesAsync(token);
            await db.ClassicWebPartUniques.Where(x => x.ScanId == assessment).ExecuteDeleteAsync(token);
            await db.ClassicPublishingSiteSummaries.Where(x => x.ScanId == assessment).ExecuteDeleteAsync(token);
            await db.ClassicSiteSummaries.Where(x => x.ScanId == assessment).ExecuteDeleteAsync(token);
            await db.ClassicPageAuditUsages.Where(x => x.ScanId == assessment).ExecuteDeleteAsync(token);
            if (!payload.Scope.Options.SkipUsageInformation)
            {
                var chunks = new List<PnP.Scanning.Core.Pipeline.Contracts.Audit.ClassicPageAuditChunkSource>();
                foreach (var member in manifest.Members.Values)
                {
                    var source = await ReadSelectedObservationAsync(db,
                        await db.SourceSnapshots.SingleAsync(x => x.SnapshotId == phase.SnapshotId, token),
                        member.ObservationId, manifest, token);
                    if (ClassicPageSourceJson.Kind(source) == "AuditChunk")
                        chunks.Add(ClassicPageSourceJson.Read<PnP.Scanning.Core.Pipeline.Contracts.Audit.ClassicPageAuditChunkSource>(source.Artifact.GetBytes()));
                }
                var succeeded = chunks.Count(x => x.Status == "succeeded");
                var status = succeeded == chunks.Count && succeeded > 0 ? "succeeded" : succeeded > 0 ? "partial" :
                    chunks.All(x => x.Status == "skipped") ? "skipped" : "failed";
                var merged = payload.Audit.GroupBy(x => x.PageUrl, StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var site in payload.Scope.Sites)
                {
                    var stats = merged.Where(group => payload.Scope.Sites.OrderByDescending(x => x.Length)
                        .FirstOrDefault(candidate => group.Key.StartsWith(candidate.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) == site).ToArray();
                    ClassicPageAuditUsage Row(string url) => new()
                    {
                        ScanId = assessment, SiteUrl = site, WebUrl = "/", PageUrl = new Uri(url).AbsolutePath,
                        AuditWindowStart = payload.Scope.AuditWindowStart, AuditWindowEnd = payload.Scope.AuditWindowEnd,
                        QueryStatus = status, SkipReason = string.Join("; ", chunks.Where(x => x.Error != null).Select(x => x.Error)),
                    };
                    foreach (var group in stats)
                    {
                        var row = Row(group.Key);
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
            var summaries = await db.ClassicWebSummaries.Where(x => x.ScanId == assessment).ToListAsync(token);
            db.ClassicSiteSummaries.AddRange(summaries.GroupBy(x => x.SiteUrl)
                .Select(group => ClassicCompatibility.BuildSite(assessment, group)));
            var scan = await db.Scans.SingleAsync(x => x.ScanId == assessment, token);
            scan.CLIMode = Services.Mode.Classic.ToString();
            scan.PreScanStatus = scan.PostScanStatus = SiteWebStatus.Finished;
            return true;
        }, token);
}
