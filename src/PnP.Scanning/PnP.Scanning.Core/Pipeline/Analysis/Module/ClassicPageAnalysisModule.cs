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

internal sealed class ClassicPageAnalysisModule : IAnalysisModule, IAnalysisSnapshotPreparation
{
    private ClassicPageInputIndex index = null!;
    private Guid assessmentId;
    private readonly WebPartMappingManager mapping = new();
    public string ModuleKey => ClassicPageModuleKey;
    private const string ClassicPageModuleKey = "classicpage";
    public string RuleVersion => "classicpage-v1";

    public async Task PrepareAsync(ISnapshotReader snapshot, CancellationToken cancellationToken)
    {
        var manifest = await snapshot.OpenAsync(cancellationToken);
        assessmentId = manifest.AssessmentId;
        async IAsyncEnumerable<SourceRecord> Records()
        {
            foreach (var id in manifest.ObservationIds) yield return await snapshot.ReadAsync(id, cancellationToken);
        }
        index = await ClassicPageInputIndex.CreateAsync(Records(), cancellationToken, manifest.AssessmentId);
    }

    public Task<AnalysisResult> AnalyzeAsync(SourceRecord source, VersionedJson parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var kind = ClassicPageSourceJson.Kind(source);
        if (kind == "Page")
        {
            var input = ClassicPageSourceJson.Read<ClassicPageItemSource>(source.Artifact.GetBytes());
            var key = ClassicPageSourceJson.WebKey(input.SiteUrl, input.WebUrl);
            var output = ClassicPageProjection.Analyze(assessmentId, index.Webs[key],
                index.Pages[key + "|" + input.DiscoveryKey], input, index.Scope.Options, mapping);
            var outcome = output.Error != null ? output.Page == null ? AnalysisOutcome.Failed : AnalysisOutcome.Unknown
                : output.Page == null ? AnalysisOutcome.NotApplicable : AnalysisOutcome.Analyzed;
            return Task.FromResult(new AnalysisResult(source.ObservationId, outcome,
                output.Error ?? output.Discovery?.AssessmentStatus ?? "Analyzed", VersionedJson.From(new { kind, output })));
        }
        if (kind == "Blog")
        {
            var input = ClassicPageSourceJson.Read<ClassicPageBlogBatchSource>(source.Artifact.GetBytes());
            var web = index.Webs[ClassicPageSourceJson.WebKey(input.SiteUrl, input.WebUrl)];
            var pages = input.Items.Select(fields =>
                ClassicBlogPageProjection.Analyze(assessmentId, input, fields, web, index.Scope.Options)).ToArray();
            return Task.FromResult(new AnalysisResult(source.ObservationId,
                input.State.Succeeded ? AnalysisOutcome.Analyzed : AnalysisOutcome.Unknown,
                input.State.Error ?? input.State.Status, VersionedJson.From(new { kind, pages })));
        }
        if (kind == "Discovery")
        {
            var input = ClassicPageSourceJson.Read<ClassicPageDiscoverySource>(source.Artifact.GetBytes());
            var files = (input.Files ?? []).Select(file => PhysicalFileAnalysisRules.Analyze(
                index.Files[(file.SiteCollectionId, file.FileUniqueId)])).ToArray();
            var coverage = input.Rows.Where(row => row.RowType is "Scope" or "Gap").ToArray();
            var incomplete = coverage.Any(row => row.DiscoveryStatus is not ("Complete" or "Empty" or "PolicyExcluded"));
            return Task.FromResult(new AnalysisResult(source.ObservationId,
                incomplete || files.Any(file => file.Classification?.Experience == "Unknown") ? AnalysisOutcome.Unknown : AnalysisOutcome.Analyzed,
                incomplete ? "Discovery coverage includes incomplete acquisition evidence." :
                    "Physical file facts classified independently of page admission.", VersionedJson.From(new { kind, files, coverage })));
        }
        if (kind == "AuditPage")
        {
            var input = ClassicPageSourceJson.Read<ClassicPageAuditPageSource>(source.Artifact.GetBytes());
            var complete = index.AuditChunks[input.Chunk].Status == "succeeded";
            var output = complete ? ClassicPageAuditAnalysis.Parse(input.Body) : [];
            return Task.FromResult(new AnalysisResult(source.ObservationId,
                complete ? AnalysisOutcome.Analyzed : AnalysisOutcome.Unknown,
                complete ? "Audit events reduced using pinned chunk coverage." : "Audit chunk acquisition is incomplete.",
                VersionedJson.From(new { kind, input.Chunk, output })));
        }
        var success = source.AcquisitionStatus == AcquisitionStatus.Complete;
        return Task.FromResult(new AnalysisResult(source.ObservationId, success ? AnalysisOutcome.NotApplicable : AnalysisOutcome.Unknown,
            source.AcquisitionError ?? (success ? "Source evidence retained" : source.AcquisitionStatus.ToString()), VersionedJson.From(new { kind })));
    }
}
