#nullable enable
using ClassicPage = PnP.Scanning.Core.Pipeline.Contracts.ClassicPageRow;
using System.Text.Json;
using System.Security.Cryptography;
using PnP.Scanning.Core.Pipeline.Contracts;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Scanners.WebPartMapping;


namespace PnP.Scanning.Core.Pipeline.Analysis;

internal sealed class ClassicPageAnalysisModule : IAnalysisModule, IAnalysisSnapshotPreparation, IAnalysisFinalizer
{
    private ClassicPageInputIndex index = null!;
    private Guid assessmentId;
    private readonly WebPartMappingManager mapping = new();
    public string ModuleKey => "classicpage";
    public string RuleVersion => "classicpage-v1";
    public async Task PrepareAsync(ISnapshotReader snapshot, CancellationToken cancellationToken)
    {
        var manifest = await snapshot.OpenAsync(cancellationToken);
        assessmentId = manifest.AssessmentId;
        async IAsyncEnumerable<SourceRecord> Records()
        {
            foreach (var id in manifest.ObservationIds) yield return await snapshot.ReadAsync(id, cancellationToken);
        }
        index = await ClassicPageInputIndex.CreateAsync(Records(), cancellationToken);
    }
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
    public Task FinalizeAsync(ISnapshotReader snapshot, IAnalysisResultReader results, IAnalysisReportWriter reports,
        VersionedJson parameters, CancellationToken cancellationToken) =>
        ClassicPageReportBuilder.PublishAsync(assessmentId, index, mapping, results, reports, cancellationToken);
}

internal sealed record ClassicAuditPageStats(string PageUrl, int Views, int Creates, int Edits, string[] UserHashes);

internal static class ClassicPageAuditAnalysis
{
    internal static ClassicAuditPageStats[] Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
            throw new SnapshotIntegrityException("Succeeded audit query has no records array.");
        var results = new Dictionary<string, (int Views, int Creates, int Edits, HashSet<string> Users)>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in values.EnumerateArray())
        {
            if (!record.TryGetProperty("operation", out var operation) || !record.TryGetProperty("objectId", out var objectId)) continue;
            var url = objectId.GetString(); var op = operation.GetString();
            if (string.IsNullOrEmpty(url) || !url.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)) continue;
            if (!results.TryGetValue(url, out var value)) value = (0, 0, 0, new(StringComparer.Ordinal));
            if (record.TryGetProperty("userId", out var user) && !string.IsNullOrEmpty(user.GetString()) && value.Users.Count < 10_000)
                value.Users.Add(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(user.GetString()!.ToUpperInvariant()))));
            results[url] = (value.Views + (string.Equals(op, "ClassicPageViewed", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                value.Creates + (string.Equals(op, "ClassicPageCreated", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                value.Edits + (string.Equals(op, "ClassicPageEdited", StringComparison.OrdinalIgnoreCase) ? 1 : 0), value.Users);
        }
        return results.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ClassicAuditPageStats(x.Key, x.Value.Views, x.Value.Creates, x.Value.Edits, x.Value.Users.OrderBy(y => y, StringComparer.Ordinal).ToArray())).ToArray();
    }
}
