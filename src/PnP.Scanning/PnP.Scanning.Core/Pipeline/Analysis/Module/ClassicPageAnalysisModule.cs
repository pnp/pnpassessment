#nullable enable
using ClassicPage = PnP.Scanning.Core.Pipeline.Contracts.Page.ClassicPageRow;
using System.Text.Json;
using System.Security.Cryptography;
using PnP.Scanning.Core.Pipeline.Analysis.Audit;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Audit;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Web;
using PnP.Scanning.Core.Pipeline.Analysis.Page.WebPartMapping;


namespace PnP.Scanning.Core.Pipeline.Analysis.Module;

/// <summary>
/// Analyzes saved Classic Pages, blog items and audit responses, then publishes reports for the selected analysis run.
/// <para>Evolved from <c>Scanners/Classic/PageScanComponent.cs</c> to separate page classification and enrichment from authenticated collection.</para>
/// </summary>
internal sealed class ClassicPageAnalysisModule : IAnalysisModule, IAnalysisSnapshotPreparation, IAnalysisFinalizer
{
    private ClassicPageInputIndex index = null!;
    private Guid assessmentId;
    private readonly WebPartMappingManager mapping = new();
    public string ModuleKey => "classicpage";
    public string RuleVersion => "classicpage-v1";
    /// <summary>
    /// Reads the selected snapshot and builds a validated index of scope, web, page and audit identities.
    /// </summary>
    public async Task PrepareAsync(ISnapshotReader snapshot, CancellationToken cancellationToken)
    {
        var manifest = await snapshot.OpenAsync(cancellationToken);
        assessmentId = manifest.AssessmentId;
        // Read exactly the pinned manifest members for the shared input index.
        async IAsyncEnumerable<SourceRecord> Records()
        {
            foreach (var id in manifest.ObservationIds) yield return await snapshot.ReadAsync(id, cancellationToken);
        }
        index = await ClassicPageInputIndex.CreateAsync(Records(), cancellationToken);
    }
    /// <summary>
    /// Dispatches one saved source by kind and returns its page projection, audit statistics or retained-evidence disposition.
    /// <para>Evolved from <c>Scanners/Classic/PageScanComponent.cs</c> to separate page classification and enrichment from authenticated collection.</para>
    /// </summary>
    public Task<AnalysisResult> AnalyzeAsync(SourceRecord source, VersionedJson parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var kind = ClassicPageSourceJson.Kind(source);
        if (kind == "Page")
        {
            var input = ClassicPageSourceJson.Read<ClassicPageItemSource>(source.Artifact.GetBytes());
            var key = ClassicPageSourceJson.WebKey(input.SiteUrl, input.WebUrl);
            var output = ClassicPageProjection.Analyze(assessmentId, index.Webs[key], index.Pages[key + "|" + input.DiscoveryKey], input, index.Scope.Options, mapping);
            var outcome = output.Error != null ? output.Page == null ? AnalysisOutcome.Failed : AnalysisOutcome.Unknown
                : output.Page == null ? AnalysisOutcome.NotApplicable : AnalysisOutcome.Analyzed;
            return Task.FromResult(new AnalysisResult(source.ObservationId, outcome, output.Error ?? output.Discovery?.AssessmentStatus ?? "Analyzed", VersionedJson.From(new { kind, output })));
        }
        if (kind == "Blog")
        {
            var input = ClassicPageSourceJson.Read<ClassicPageBlogBatchSource>(source.Artifact.GetBytes());
            var web = index.Webs[ClassicPageSourceJson.WebKey(input.SiteUrl, input.WebUrl)];
            var pages = input.Items.Select(fields => BlogPage(input, fields, web)).Where(x => x != null).ToArray();
            return Task.FromResult(new AnalysisResult(source.ObservationId, AnalysisOutcome.Analyzed, "Blog items projected", VersionedJson.From(new { kind, pages })));
        }
        if (kind == "AuditPage")
        {
            var input = ClassicPageSourceJson.Read<ClassicPageAuditPageSource>(source.Artifact.GetBytes());
            var output = index.AuditChunks[input.Chunk].Status == "succeeded" ? ClassicPageAuditAnalysis.Parse(input.Body) : [];
            return Task.FromResult(new AnalysisResult(source.ObservationId, AnalysisOutcome.Analyzed, "Audit events reduced using pinned chunk coverage", VersionedJson.From(new { kind, input.Chunk, output })));
        }
        var success = source.AcquisitionStatus == AcquisitionStatus.Complete;
        return Task.FromResult(new AnalysisResult(source.ObservationId, success ? AnalysisOutcome.NotApplicable : AnalysisOutcome.Unknown,
            source.AcquisitionError ?? (success ? "Source evidence retained for finalization" : source.AcquisitionStatus.ToString()), VersionedJson.From(new { kind })));
    }
    /// <summary>
    /// Projects one saved blog item into a page report row while applying the pinned home-page and user-information options.
    /// <para>Evolved from <c>Scanners/Classic/PageScanComponent.cs</c> to separate page classification and enrichment from authenticated collection.</para>
    /// </summary>
    private ClassicPage? BlogPage(ClassicPageBlogBatchSource input, Dictionary<string, SourceField> fields, ClassicPageWebSource web)
    {
        var url = fields.GetValueOrDefault("FileRef")?.Text ?? fields.GetValueOrDefault("ID")?.Text ?? "";
        var home = web.WelcomePageState.Succeeded ? HomePageDetector.IsHomePage(url, web.WelcomePage) : (bool?)null;
        if (index.Scope.Options.HomePageOnly && home != true) return null;
        return new ClassicPage
        {
            ScanId = assessmentId, SiteUrl = input.SiteUrl, WebUrl = input.WebUrl, PageUrl = url, PageName = fields.GetValueOrDefault("Title")?.Text ?? "",
            ListId = input.ListId, ListTitle = input.ListTitle, ListUrl = input.ListUrl,
            ModifiedAt = fields.TryGetValue("Modified", out var modified) && modified.ToValue() is DateTime date ? date : default,
            ModifiedBy = index.Scope.Options.SkipUserInformation ? null : ClassicPageProjection.ModifiedBy(fields),
            PageType = ClassicPageRules.BlogPage, HomePage = home, RemediationCode = "CP4",
        };
    }
    /// <summary>
    /// Combines committed per-source results into Classic Pages report rows for this run.
    /// <para>Evolved from <c>Scanners/Classic/ClassicScanner.cs</c> to run report aggregation after offline analysis rather than after online scanning.</para>
    /// </summary>
    public Task FinalizeAsync(ISnapshotReader snapshot, IAnalysisResultReader results, IAnalysisReportWriter reports,
        VersionedJson parameters, CancellationToken cancellationToken) =>
        ClassicPageReportBuilder.PublishAsync(assessmentId, index, mapping, results, reports, cancellationToken);
}
