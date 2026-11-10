#nullable enable
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.ClassicPages.Compatibility;
using PnP.Scanning.Core.ClassicPages.Contracts.Audit;
using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;

namespace PnP.Scanning.Core.Storage.Pipeline;

/// <summary>
/// Binds a completed native publication and audit coverage to the exact immutable analysis and source snapshot used for export.
/// </summary>
internal sealed record ClassicReportSource(
    Guid AssessmentId,
    Guid AnalysisRunId,
    Guid SnapshotId,
    ClassicCompatibilityPayload Publication,
    ClassicPageAuditChunkSource[] AuditChunks)
{
    public IReadOnlyDictionary<PageIdentity, ClassicPageMeasurements> Measurements { get; init; } =
        new Dictionary<PageIdentity, ClassicPageMeasurements>();
}

/// <summary>
/// Exposes verified Classic report inputs without changing the generic storage schema or acquiring live data.
/// </summary>
internal sealed partial class PipelineStore
{
    /// <summary>
    /// Selects the latest Classic analysis, verifies its immutable manifest/report digest, and rejects incomplete newer runs.
    /// A null result means a historical assessment has no native pipeline and must retain explicitly labelled legacy reporting.
    /// </summary>
    internal static async Task<ClassicReportSource?> ReadClassicReportSourceAsync(
        ScanContext context,
        Guid assessmentId,
        CancellationToken token)
    {
        PhaseRunRow? phase = await context.PhaseRuns.AsNoTracking()
            .Where(run => run.AssessmentId == assessmentId && run.ModuleKey == "classicpage" &&
                          run.Kind == PhaseKind.Analysis)
            .OrderByDescending(run => run.CreatedAtUtc)
            .ThenByDescending(run => run.RunId)
            .FirstOrDefaultAsync(token);
        if (phase == null)
        {
            if (await context.PhaseRuns.AnyAsync(
                    run => run.AssessmentId == assessmentId && run.ModuleKey == "classicpage",
                    token))
                throw new InvalidOperationException(
                    "Classic CSV export requires a completed native analysis publication.");
            return null;
        }

        if (phase.Status != ScanStatus.Finished)
            throw new InvalidOperationException(
                "The latest Classic analysis is incomplete; older report rows cannot substitute for it.");
        if (phase.InputVersion != ClassicContentContract.InputVersion ||
            phase.RuleVersion != ClassicContentContract.RuleVersion)
            throw new NotSupportedException(
                "Native CSV reporting requires the current Classic source/rule contract; older sealed evidence is not reinterpreted.");
        PinnedManifest manifest = await AnalysisManifestAsync(context, phase.RunId, token);
        IReadOnlyList<AnalysisReportRow> rows = await ReadReportsAsync(context, phase.RunId, token);
        AnalysisReportRow publication = rows.Single(row => row.Kind == "ClassicCompatibility");
        ClassicCompatibilityPayload payload = publication.Payload.Value.Deserialize<ClassicCompatibilityPayload>() ??
                                              throw new SnapshotIntegrityException("Classic publication is missing.");
        List<ClassicPageAuditChunkSource> chunks = [];
        Dictionary<PageIdentity, ClassicPageMeasurements> measurements = [];
        SourceSnapshotRow snapshot = await context.SourceSnapshots.AsNoTracking()
            .SingleAsync(row => row.SnapshotId == phase.SnapshotId, token);
        foreach (ManifestMember member in manifest.Members.Values.Where(member =>
                     member.SourceIdentity.StartsWith("AuditChunk:", StringComparison.Ordinal) ||
                     member.SourceIdentity.StartsWith("FileEnrichment:", StringComparison.Ordinal)))
        {
            SourceRecord record = await ReadSelectedObservationAsync(
                context,
                snapshot,
                member.ObservationId,
                manifest,
                token);
            if (CollectionJson.Kind(record) == "AuditChunk")
            {
                chunks.Add(CollectionJson.Read<ClassicPageAuditChunkSource>(record.Artifact.GetBytes()));
                continue;
            }

            if (CollectionJson.Kind(record) != "FileEnrichment")
                throw new SnapshotIntegrityException("Report measurement receipt has a different acquisition kind.");
            FilePageEvidence input = CollectionJson.Read<FilePageEvidence>(record.Artifact.GetBytes());
            ClassicPageDiscovery? disposition = payload.Discovery.SingleOrDefault(row => row.RowType == "Page" &&
                row.RecordKey == input.DiscoveryKey && row.SiteUrl == input.SiteUrl && row.WebUrl == input.WebUrl);
            if (disposition?.SiteCollectionId is not Guid siteId || disposition.FileUniqueId is not Guid fileId)
                continue;
            PageIdentity identity = PageIdentity.File(siteId, fileId);
            ClassicPageRow? page = payload.Pages.SingleOrDefault(page => page.Identity == identity);
            if (page == null) continue;
            WebFacts web = payload.Webs.Single(web => web.SiteCollectionId == siteId && web.WebId == page.WebId);
            int parts = payload.Parts.Count(part =>
                part.SiteUrl == page.SiteUrl && part.WebUrl == page.WebUrl && part.PageUrl == page.PageUrl);
            measurements.Add(identity, ClassicPageMeasurements.FromCaptured(input, page, disposition, web, parts));
        }

        return new(assessmentId, phase.RunId, phase.SnapshotId, payload, chunks.ToArray())
        {
            Measurements = measurements
        };
    }
}