using PnP.Scanning.Core.ClassicPages.Compatibility;
using PnP.Scanning.Core.ClassicPages.Contracts.Module;
#nullable enable
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.ClassicPages.Analysis.Audit;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;


namespace PnP.Scanning.Core.Storage.Pipeline;

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
                    List<Core.ClassicPages.Contracts.Audit.ClassicPageAuditChunkSource>? chunks =
                        new List<PnP.Scanning.Core.ClassicPages.Contracts.Audit.ClassicPageAuditChunkSource>();
                    SourceSnapshotRow snapshot = await db.SourceSnapshots.AsNoTracking()
                        .SingleAsync(x => x.SnapshotId == phase.SnapshotId, token);
                    // Full manifest validation above already verified every member, including non-audit content.
                    foreach (ManifestMember member in manifest.Members.Values.Where(member =>
                                 new VersionedJson(member.MetadataJson).Value.GetProperty("kind").GetString() == "AuditChunk"))
                    {
                        SourceRecord? source = await ReadSelectedObservationAsync(
                            db,
                            snapshot,
                            member.ObservationId,
                            manifest,
                            token);
                        if (CollectionJson.Kind(source) == "AuditChunk")
                            chunks.Add(
                                CollectionJson
                                    .Read<PnP.Scanning.Core.ClassicPages.Contracts.Audit.ClassicPageAuditChunkSource>(
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
                    IGrouping<string, ClassicAuditPageStats>[]? merged = payload.Audit.GroupBy(
                            x => AuditCompatibilityLocator(x.PageUrl, payload.Scope.SiteCollectionUrls),
                            ClassicPageAuditAnalysis.LocatorComparer)
                        .ToArray();
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
                                PageUrl = AuditServerRelativeLocator(url),
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
                await StorageManager.PopulateWebPartUniqueAsync(db, assessment, ClassicCompatibility.MappingManager);
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

    /// <summary>
    /// Attributes server-relative audit addresses only to the uniquely declared HTTPS authority.
    /// Raw publication keys remain unchanged; query-addressed items are never folded into their renderer path.
    /// </summary>
    internal static string AuditCompatibilityLocator(string locator, string[] sites)
    {
        if (!locator.StartsWith('/')) return locator;
        if (locator.StartsWith("//", StringComparison.Ordinal))
            throw new SnapshotIntegrityException("A scheme-relative audit locator has no verified owner.");
        string[] authorities = sites.Select(site =>
            {
                Uri uri = new(site);
                if (uri.Scheme != "https" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new SnapshotIntegrityException("Audit attribution requires a declared HTTPS authority.");
                return uri.GetLeftPart(UriPartial.Authority);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (authorities.Length != 1)
            throw new SnapshotIntegrityException("A server-relative audit locator has no unique declared authority.");
        return authorities[0] + locator;
    }

    /// <summary>
    /// Removes only the verified HTTPS origin for legacy presentation, retaining the original path and opaque query/fragment bytes.
    /// URI parsing validates the locator but never rewrites or drops its suffix.
    /// </summary>
    internal static string AuditServerRelativeLocator(string locator)
    {
        if (!Uri.TryCreate(locator, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new SnapshotIntegrityException("Legacy audit presentation requires a verified HTTPS locator.");
        int authorityStart = locator.IndexOf("://", StringComparison.Ordinal) + 3;
        int suffix = locator.IndexOfAny(['/', '?', '#'], authorityStart);
        if (suffix < 0) return "/";
        return locator[suffix] == '/' ? locator[suffix..] : "/" + locator[suffix..];
    }
}