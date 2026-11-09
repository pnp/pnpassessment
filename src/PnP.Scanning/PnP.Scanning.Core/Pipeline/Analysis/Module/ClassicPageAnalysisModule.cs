#nullable enable
using System.Text.Json;
using PnP.Scanning.Core.Pipeline.Analysis.Audit;
using PnP.Scanning.Core.Pipeline.Contracts.Audit;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Scanners.WebPartMapping;

namespace PnP.Scanning.Core.Pipeline.Analysis.Module;

/// <summary>
/// Analyzes detached Classic page evidence and publishes deterministic compatibility results.
/// </summary>
internal sealed class ClassicPageAnalysisModule : IAnalysisModule, IAnalysisSnapshotPreparation, IAnalysisFinalizer
{
    private ClassicPageInputIndex index = null!;
    private Guid assessmentId;
    private readonly WebPartMappingManager mapping = new();
    public string ModuleKey => ClassicPageModuleKey;
    private const string ClassicPageModuleKey = "classicpage";
    public string RuleVersion => "classicpage-v2";

    /// <summary>
    /// Publishes existing-report compatibility results from fixed source and committed analysis output.
    /// </summary>
    public Task FinalizeAsync(
        ISnapshotReader snapshot,
        IAnalysisResultReader results,
        IAnalysisReportWriter reports,
        VersionedJson parameters,
        CancellationToken token) =>
        Storage.Pipeline.ClassicCompatibility.PublishAsync(assessmentId, index, results, reports, token);

    /// <summary>
    /// Validates the selected snapshot and builds shared detached source indexes.
    /// </summary>
    public async Task PrepareAsync(ISnapshotReader snapshot, CancellationToken cancellationToken)
    {
        SnapshotInfo? manifest = await snapshot.OpenAsync(cancellationToken);
        assessmentId = manifest.AssessmentId;

        async IAsyncEnumerable<SourceRecord> Records()
        {
            foreach (Guid id in manifest.ObservationIds) yield return await snapshot.ReadAsync(id, cancellationToken);
        }

        index = await ClassicPageInputIndex.CreateAsync(Records(), cancellationToken, manifest.AssessmentId);
    }

    /// <summary>
    /// Dispatches one saved source kind and returns its deterministic result with an explicit outcome.
    /// </summary>
    public Task<AnalysisResult> AnalyzeAsync(
        SourceRecord source,
        VersionedJson parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? kind = ClassicPageSourceJson.Kind(source);
        if (kind == "Page")
        {
            ClassicPageItemSource? input =
                ClassicPageSourceJson.Read<ClassicPageItemSource>(source.Artifact.GetBytes());
            string? key = ClassicPageSourceJson.WebKey(input.SiteUrl, input.WebUrl);
            ClassicPageProjectionResult? output = ClassicPageProjection.Analyze(
                assessmentId,
                index.Webs[key],
                index.Pages[key + "|" + input.DiscoveryKey],
                input,
                index.Scope.Options,
                mapping);
            AnalysisOutcome outcome = output.Error != null
                ? output.Page == null ? AnalysisOutcome.Failed : AnalysisOutcome.Unknown
                : output.Page == null
                    ? AnalysisOutcome.NotApplicable
                    : AnalysisOutcome.Analyzed;
            return Task.FromResult(
                new AnalysisResult(
                    source.ObservationId,
                    outcome,
                    output.Error ?? output.Discovery?.AssessmentStatus ?? "Analyzed",
                    VersionedJson.From(new { kind, output })));
        }

        if (kind == "Blog")
        {
            ClassicPageBlogBatchSource? input =
                ClassicPageSourceJson.Read<ClassicPageBlogBatchSource>(source.Artifact.GetBytes());
            Contracts.Web.ClassicPageWebSource? web = index.Webs[ClassicPageSourceJson.WebKey(
                input.SiteUrl,
                input.WebUrl)];
            ClassicPageRow?[]? pages = input.Items.Select(fields =>
                    ClassicBlogPageProjection.Analyze(assessmentId, input, fields, web, index.Scope.Options))
                .ToArray();
            return Task.FromResult(
                new AnalysisResult(
                    source.ObservationId,
                    input.State.Succeeded ? AnalysisOutcome.Analyzed : AnalysisOutcome.Unknown,
                    input.State.Error ?? input.State.Status,
                    VersionedJson.From(new { kind, pages })));
        }

        if (kind == "Discovery")
        {
            ClassicPageDiscoverySource? input =
                ClassicPageSourceJson.Read<ClassicPageDiscoverySource>(source.Artifact.GetBytes());
            PhysicalFileAnalysis[]? files = (input.Files ?? []).Select(file => PhysicalFileAnalysisRules.Analyze(
                    index.Files[(file.SiteCollectionId, file.FileUniqueId)]))
                .ToArray();
            ClassicPageDiscoveryRow[]? coverage = input.Rows.Where(row => row.RowType is "Scope" or "Gap").ToArray();
            bool incomplete =
                coverage.Any(row => row.DiscoveryStatus is not ("Complete" or "Empty" or "PolicyExcluded"));
            return Task.FromResult(
                new AnalysisResult(
                    source.ObservationId,
                    incomplete || files.Any(file => file.Classification?.Experience == "Unknown")
                        ? AnalysisOutcome.Unknown
                        : AnalysisOutcome.Analyzed,
                    incomplete
                        ? "Discovery coverage includes incomplete acquisition evidence."
                        : "Physical file facts classified independently of page admission.",
                    VersionedJson.From(new { kind, files, coverage })));
        }

        if (kind == "AuditPage")
        {
            ClassicPageAuditPageSource? input =
                ClassicPageSourceJson.Read<ClassicPageAuditPageSource>(source.Artifact.GetBytes());
            bool complete = index.AuditChunks[input.Chunk].Status == "succeeded";
            ClassicAuditPageStats[]? output = complete ? ClassicPageAuditAnalysis.Parse(input.Body) : [];
            return Task.FromResult(
                new AnalysisResult(
                    source.ObservationId,
                    complete ? AnalysisOutcome.Analyzed : AnalysisOutcome.Unknown,
                    complete
                        ? "Audit events reduced using pinned chunk coverage."
                        : "Audit chunk acquisition is incomplete.",
                    VersionedJson.From(new { kind, input.Chunk, output })));
        }

        bool success = source.AcquisitionStatus == AcquisitionStatus.Complete;
        return Task.FromResult(
            new AnalysisResult(
                source.ObservationId,
                success ? AnalysisOutcome.NotApplicable : AnalysisOutcome.Unknown,
                source.AcquisitionError ?? (success ? "Source evidence retained" : source.AcquisitionStatus.ToString()),
                VersionedJson.From(new { kind })));
    }
}