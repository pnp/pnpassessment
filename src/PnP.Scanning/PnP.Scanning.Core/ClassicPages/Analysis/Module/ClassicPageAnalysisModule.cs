using PnP.Scanning.Core.ClassicPages.Contracts.Module;
#nullable enable
using PnP.Scanning.Core.ClassicPages.Analysis.Audit;
using PnP.Scanning.Core.ClassicPages.Contracts.Audit;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Scanners.WebPartMapping;

namespace PnP.Scanning.Core.ClassicPages.Analysis.Module;

/// <summary>
/// Analyzes detached Classic page evidence and publishes deterministic compatibility results.
/// </summary>
internal sealed class ClassicPageAnalysisModule : IAnalysisModule, IAnalysisSnapshotPreparation, IAnalysisFinalizer
{
    private ClassicContentIndex index = null!;
    private Guid assessmentId;
    private readonly WebPartMappingManager mapping = new();
    public string ModuleKey => ClassicContentContract.ModuleKey;
    public string RuleVersion => ClassicContentContract.RuleVersion;

    /// <summary>
    /// Publishes existing-report compatibility results from fixed source and committed analysis output.
    /// </summary>
    public async Task FinalizeAsync(
        ISnapshotReader snapshot,
        IAnalysisResultReader results,
        IAnalysisReportWriter reports,
        VersionedJson parameters,
        CancellationToken token)
    {
        ClassicPagePublication output = await ClassicPagePublication.BuildAsync(assessmentId, index, results, token);
        await ClassicPages.Compatibility.ClassicCompatibility.PublishAsync(output, reports, token);
    }

    /// <summary>
    /// Validates the selected snapshot and builds shared detached source indexes.
    /// </summary>
    public async Task PrepareAsync(ISnapshotReader snapshot, CancellationToken cancellationToken)
    {
        SnapshotInfo? manifest = await snapshot.OpenAsync(cancellationToken);
        if (manifest.ModuleKey != ModuleKey || manifest.InputVersion != ClassicContentContract.InputVersion)
            throw new NotSupportedException(
                $"Rule '{RuleVersion}' requires '{ClassicContentContract.InputVersion}'; snapshot '{manifest.InputVersion}' is not reinterpreted as the new object contract.");
        assessmentId = manifest.AssessmentId;

        async IAsyncEnumerable<SourceRecord> Records()
        {
            foreach (Guid id in manifest.ObservationIds) yield return await snapshot.ReadAsync(id, cancellationToken);
        }

        index = await ClassicContentIndex.CreateAsync(Records(), cancellationToken, manifest.AssessmentId);
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
        string? kind = CollectionJson.Kind(source);
        if (kind == "FileEnrichment")
        {
            FilePageEvidence? input = CollectionJson.Read<FilePageEvidence>(source.Artifact.GetBytes());
            string? key = CollectionJson.WebKey(input.SiteUrl, input.WebUrl);
            FilePageAnalysis? output = FilePageProjection.Analyze(
                assessmentId,
                index.Webs[key],
                index.FileCandidates[key + "|" + input.DiscoveryKey],
                input,
                index.Scope.Options,
                mapping);
            AnalysisOutcome outcome = FileOutcome(output);
            return Task.FromResult(
                new AnalysisResult(
                    source.ObservationId,
                    outcome,
                    output.Error ?? output.Discovery?.AssessmentStatus ?? "Analyzed",
                    VersionedJson.From(new { kind, output })));
        }

        if (kind == "ListItems")
        {
            ListItemBatch? input = CollectionJson.Read<ListItemBatch>(source.Artifact.GetBytes());
            Contracts.Web.WebFacts? web = index.Webs[CollectionJson.WebKey(input.SiteUrl, input.WebUrl)];
            ClassicPageRow?[] pages = input.Items.Select(item =>
                    ListItemPageProjection.Analyze(assessmentId, input, item, web, index.Scope.Options))
                .ToArray();
            return Task.FromResult(
                new AnalysisResult(
                    source.ObservationId,
                    input.State.Succeeded ? AnalysisOutcome.Analyzed : AnalysisOutcome.Unknown,
                    input.State.Error ?? input.State.Status,
                    VersionedJson.From(new { kind, pages })));
        }

        if (kind == "FileInventory")
        {
            WebFileInventory? input = CollectionJson.Read<WebFileInventory>(source.Artifact.GetBytes());
            PhysicalFileAnalysis[]? files = (input.Files ?? []).Select(file => PhysicalFileAnalysisRules.Analyze(
                    index.Files[(file.SiteCollectionId, file.FileUniqueId)]))
                .ToArray();
            DiscoveryEvidence[]? coverage = input.Evidence
                .Where(row => row.RowType is DiscoveryRowType.Scope or DiscoveryRowType.Gap)
                .ToArray();
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
                CollectionJson.Read<ClassicPageAuditPageSource>(source.Artifact.GetBytes());
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

    /// <summary>Distinguishes inapplicable modern content, failed enrichment and partially assessed Pages without masking errors.</summary>
    private static AnalysisOutcome FileOutcome(FilePageAnalysis output)
    {
        if (output.Modern) return AnalysisOutcome.NotApplicable;
        if (output.Error != null) return output.Page == null ? AnalysisOutcome.Failed : AnalysisOutcome.Unknown;
        return output.Page == null ? AnalysisOutcome.NotApplicable : AnalysisOutcome.Analyzed;
    }
}