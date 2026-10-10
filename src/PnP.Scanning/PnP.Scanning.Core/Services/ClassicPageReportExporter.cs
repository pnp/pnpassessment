#nullable enable
using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;

namespace PnP.Scanning.Core.Services;

/// <summary>Exports one published run. Legacy tables and the latest assessment status never select its inputs.</summary>
internal static class ClassicPageReportExporter
{
    internal static async Task<(Guid RunId, string Path)> ExportAsync(PipelineStore store, Guid assessmentId, Guid? selectedRun,
        string? exportPath, string? delimiter, bool powerBi, CancellationToken token)
    {
        await store.EnsureDatabaseAsync(assessmentId, token);
        using var db = store.CreateContext(assessmentId);
        var query = db.PhaseRuns.AsNoTracking().Where(x => x.Kind == PhaseKind.Analysis && x.ModuleKey == "classicpage");
        var run = selectedRun.HasValue ? await query.SingleOrDefaultAsync(x => x.RunId == selectedRun.Value, token)
            : await query.Where(x => x.Status == ScanStatus.Finished).OrderByDescending(x => x.EndedAtUtc).ThenByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(token);
        if (run == null || run.Status != ScanStatus.Finished) throw new InvalidOperationException("The selected Classic Page analysis has no completed report yet.");
        var rows = await db.ClassicPageReportRows.AsNoTracking().Where(x => x.AnalysisRunId == run.RunId).OrderBy(x => x.Ordinal).ToListAsync(token);
        if (rows.Count == 0) throw new SnapshotIntegrityException("The completed analysis has no published report projection.");
        var snapshot = await store.OpenSnapshotAsync(assessmentId, run.SnapshotId, token);
        var analysis = await db.AnalysisRuns.AsNoTracking().SingleAsync(x => x.AnalysisRunId == run.RunId, token);
        if (snapshot.ManifestDigest != analysis.ManifestDigest ||
            AnalysisReportDigest.Compute(rows.Select(x => new AnalysisReportRow(x.Kind, x.RowKey, x.Ordinal, new VersionedJson(x.PayloadJson)))) !=
            new VersionedJson(run.CheckpointJson!).Value.GetProperty("reportDigest").GetString())
            throw new SnapshotIntegrityException("Report projections or selected analysis input changed after publication.");
        var path = string.IsNullOrWhiteSpace(exportPath) ? System.IO.Path.Combine(store.RootDirectory, assessmentId.ToString(), "report", run.RunId.ToString()) : System.IO.Path.GetFullPath(exportPath);
        Directory.CreateDirectory(path);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = string.IsNullOrEmpty(delimiter) ? "," : delimiter };
        async Task Write<T>(string kind, IEnumerable<T>? values = null)
        {
            token.ThrowIfCancellationRequested();
            using var writer = new StreamWriter(System.IO.Path.Combine(path, kind + ".csv"));
            using var csv = new CsvWriter(writer, config);
            if (typeof(T) == typeof(ClassicPageAuditUsage)) csv.Context.RegisterClassMap<AuditMap>();
            var records = values ?? rows.Where(x => x.Kind == kind).OrderBy(x => x.Ordinal)
                .Select(x => new VersionedJson(x.PayloadJson).Value.Deserialize<T>()!);
            // Preserve a usable schema even when a selected component has no rows.
            csv.WriteHeader<T>(); await csv.NextRecordAsync();
            foreach (var row in records) { token.ThrowIfCancellationRequested(); csv.WriteRecord(row); await csv.NextRecordAsync(); }
        }
        var scan = await db.Scans.AsNoTracking().SingleAsync(x => x.ScanId == assessmentId, token);
        scan.Status = ScanStatus.Finished; scan.CLIMode = Mode.ClassicPage.ToString(); scan.StartDate = run.StartedAtUtc ?? run.CreatedAtUtc; scan.EndDate = run.EndedAtUtc ?? default;
        // Credentials are never necessary report inputs.
        scan.CLICertFilePassword = null;
        await Write("scans", new[] { scan });
        await Write<Property>("properties", await db.Properties.AsNoTracking().ToListAsync(token));
        await Write<History>("history", []);
        await Write<SiteCollection>("sitecollections"); await Write<Web>("webs");
        await Write<ClassicPageDiscovery>("discovery"); await Write<ClassicPage>("classicpages");
        await Write<ClassicPageWebPart>("classicpagewebparts"); await Write<ClassicWebPartUnique>("classicwebpartunique");
        await Write<ClassicWebSummary>("classicwebsummaries"); await Write<ClassicSiteSummary>("classicsitesummaries");
        await Write<ClassicPublishingSiteSummary>("classicpublishingsitesummaries");
        await Write<Workflow>("workflows", []); await Write<ClassicInfoPath>("classicinfopath", []);
        await Write<ClassicList>("classiclists", []); await Write<ClassicUserCustomAction>("classicusercustomactions", []);
        await Write<ClassicExtensibility>("classicextensibilities", []);
        if (rows.Any(x => x.Kind == "classicpageauditusage")) await Write<ClassicPageAuditUsage>("classicpageauditusage");
        else
        {
            var previous = System.IO.Path.Combine(path, "classicpageauditusage.csv");
            if (File.Exists(previous)) File.Delete(previous);
        }
        var receipt = new { assessmentId, analysisRunId = run.RunId, run.SnapshotId, run.RuleVersion,
            manifestDigest = (await db.AnalysisRuns.AsNoTracking().SingleAsync(x => x.AnalysisRunId == run.RunId, token)).ManifestDigest,
            inputVersion = run.InputVersion };
        await File.WriteAllTextAsync(System.IO.Path.Combine(path, "analysis-run.json"), JsonSerializer.Serialize(receipt), token);
        if (powerBi)
        {
            var file = System.IO.Path.Combine(path, "ClassicAssessmentReport.pbit");
            ReportManager.PersistPBitFromResource("PnP.Scanning.Core.Scanners.Classic.ClassicAssessmentReport.pbit", file);
            ReportManager.RewriteDataLocationsInPbit(file, config.Delimiter, "q:\\\\github\\\\pnpassessment\\\\src\\\\PnP.Scanning\\\\Reports\\\\Classic\\\\", ",");
            return (run.RunId, file);
        }
        return (run.RunId, path);
    }
    private sealed class AuditMap : ClassMap<ClassicPageAuditUsage>
    {
        public AuditMap() { AutoMap(CultureInfo.InvariantCulture); Map(x => x.WebUrl).Ignore(); }
    }
}
