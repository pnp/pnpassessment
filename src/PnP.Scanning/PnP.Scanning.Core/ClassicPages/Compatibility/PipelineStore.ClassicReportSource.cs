#nullable enable
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.ClassicPages.Compatibility;
using PnP.Scanning.Core.ClassicPages.Contracts.Audit;
using PnP.Scanning.Core.ClassicPages.Contracts.Module;
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
    ClassicPageAuditChunkSource[] AuditChunks);

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
        SourceSnapshotRow snapshot = await context.SourceSnapshots.AsNoTracking()
            .SingleAsync(row => row.SnapshotId == phase.SnapshotId, token);
        foreach (ManifestMember member in manifest.Members.Values.Where(member =>
                     member.SourceIdentity.StartsWith("AuditChunk:", StringComparison.Ordinal)))
        {
            SourceRecord record = await ReadSelectedObservationAsync(
                context,
                snapshot,
                member.ObservationId,
                manifest,
                token);
            chunks.Add(CollectionJson.Read<ClassicPageAuditChunkSource>(record.Artifact.GetBytes()));
        }

        return new(assessmentId, phase.RunId, phase.SnapshotId, payload, chunks.ToArray());
    }
}