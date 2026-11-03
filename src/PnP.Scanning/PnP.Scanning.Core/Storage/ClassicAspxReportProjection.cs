#nullable enable
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners.Classic.Pages;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PnP.Scanning.Core.Storage;

/// <summary>Streams raw SPFile facts once and applies the existing classic classification only in the business export layer.</summary>
internal static class ClassicAspxReportProjection
{
    internal const string ClassicAspxCsv = "classicaspx.csv";
    internal const string SummaryJson = "classicaspxsummary.json";
    private const string SummarySchemaVersion = "classic-aspx-summary/v1";
    private const string SummaryCountBasis = "Selected unique physical ASPX files after existing modern-page exclusions in this assessment stream";

    private static class AcquisitionStates
    {
        internal const string DeclaredScopeObserved = "DeclaredScopeObserved";
        internal const string ObservedWithoutNativeMarker = "ObservedWithoutNativeMarker";
        internal const string NotAcquiredLegacy = "NotAcquired/Legacy";
    }

    private static class SummaryStates
    {
        internal const string Unknown = "Unknown";
        internal const string FrozenPolicy = "FrozenPolicy";
        internal const string NotAsserted = "NotAsserted";
    }

    /// <summary>Reads one scan-scoped, untracked SQL stream in native site/file GUID order, without joining assessed pages or persisting business classifications.</summary>
    internal static IQueryable<SPFile> Files(ScanContext context, Guid scanId) =>
        context.SPFiles.AsNoTracking()
            .Where(file => file.ScanId == scanId)
            .OrderBy(file => file.SiteCollectionId)
            .ThenBy(file => file.FileUniqueId);

    /// <summary>Selects only observed ASPX files and excludes only existing modern results, retaining unknown and failed metadata observations.</summary>
    internal static ClassicAspxExport? Project(SPFile file)
    {
        if (!SharePointAspxUrlPolicy.IsAspxPath(file.FileName) &&
            !SharePointAspxUrlPolicy.IsAspxPath(file.ServerRelativeUrl))
        {
            return null;
        }
        var classification = ClassicAspxExportClassifier.Classify(file);
        if (classification.IsModern)
        {
            return null;
        }
        var poison = ClassicAspxPoisonAnalysis.Analyze(file);
        return new ClassicAspxExport
        {
            ScanId = file.ScanId,
            SiteCollectionId = file.SiteCollectionId,
            WebId = file.WebId,
            ListId = file.ListId,
            FileUniqueId = file.FileUniqueId,
            SiteUrl = file.SiteUrl,
            WebUrl = file.WebUrl,
            Url = NormalizeUrl(file),
            FileName = file.FileName,
            ClassicPageType = classification.PageType,
            PageRole = SetupPathClassifier.Classify(file.SetupPath).ToString(),
            ClassificationState = classification.State,
            ClassificationReason = classification.Reason,
            ContentTypeId = file.ContentTypeId,
            CustomizedPageStatus = file.CustomizedPageStatus,
            SetupPath = file.SetupPath,
            NoExecute = poison.IsPoisoned,
            MetadataReadState = file.ItemMetadataReadState,
            ErrorCodes = file.FileErrorCodes,
            ErrorDetail = file.FileErrorDetail,
            ObservedAtUtc = file.ObservedAtUtc
        };
    }

    /// <summary>Uses the existing decoded-physical-path policy only for the exported URL, leaving the stored locator and GUID identity untouched.</summary>
    private static string? NormalizeUrl(SPFile file)
    {
        if (!Uri.TryCreate(file.SiteUrl, UriKind.Absolute, out var site))
        {
            return null;
        }
        try
        {
            SharePointAspxUrlPolicy.ValidateWeb(site);
        }
        catch (ArgumentException)
        {
            return null;
        }
        var policy = new SharePointAspxUrlPolicy(site, site);
        return policy.TryNormalizeDecodedPhysicalPath(file.ServerRelativeUrl, out var path) ? path : null;
    }

    /// <summary>Writes only the canonical business CSV and its summary, counting selected rows without asserting complete DocFlags collection or tenant completeness.</summary>
    internal static async Task ExportCsvAsync(ScanContext context, Guid scanId, string exportPath,
        CsvConfiguration configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(exportPath, ClassicAspxCsv);
        var counts = await WriteCsvAsync(context, scanId, path, configuration, cancellationToken).ConfigureAwait(false);
        var declaration = await ClassicAspxProjectionContract.ReadAsync(context, scanId, cancellationToken).ConfigureAwait(false);
        var exclusions = await context.Properties.AsNoTracking()
            .Where(row => row.ScanId == scanId && row.Name == Constants.StartExcludeSiteTemplates)
            .Select(row => row.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var acquisitionState = declaration.IsRecognized
            ? AcquisitionStates.DeclaredScopeObserved
            : counts.SawPhysicalObservation ? AcquisitionStates.ObservedWithoutNativeMarker : AcquisitionStates.NotAcquiredLegacy;
        var evidence = await FileEvidenceAsync(path, cancellationToken).ConfigureAwait(false);
        var summary = new
        {
            schemaVersion = SummarySchemaVersion,
            scanId,
            projectionContract = declaration.Version,
            acquisitionState,
            scopeMode = declaration.ScopeMode ?? SummaryStates.Unknown,
            templateExclusions = new
            {
                state = exclusions == null ? SummaryStates.Unknown : SummaryStates.FrozenPolicy,
                value = exclusions
            },
            selectedClassicAspxCount = counts.Selected,
            unknownClassificationCount = counts.UnknownClassification,
            poisonedCount = counts.Poisoned,
            poisonUnknownCount = counts.PoisonUnknown,
            noExecuteMissingCount = counts.NoExecuteMissing,
            countBasis = SummaryCountBasis,
            coverage = new
            {
                targeted = declaration.ScopeMode == ClassicAspxProjectionContract.HomePageOnly,
                tenantCompleteness = SummaryStates.NotAsserted,
                selectedFileStates = counts.CoverageStates
            },
            nullableStringEncoding = ClassicAspxNullableStringConverter.EncodingVersion,
            poisonEvidenceKind = ClassicAspxPoisonAnalysis.EvidenceKind,
            fullDocFlagsCollected = false,
            files = new[] { evidence }
        };
        await File.WriteAllTextAsync(Path.Combine(exportPath, SummaryJson), JsonSerializer.Serialize(summary,
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enumerates SQL once with cancellation and constant row memory, writing the fixed header even when no business row is selected.</summary>
    private static async Task<ExportCounts> WriteCsvAsync(ScanContext context, Guid scanId, string path,
        CsvConfiguration configuration, CancellationToken cancellationToken)
    {
        var counts = new ExportCounts();
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        using var csv = new CsvWriter(writer, configuration);
        csv.WriteHeader<ClassicAspxExport>();
        await csv.NextRecordAsync().ConfigureAwait(false);
        await foreach (var file in Files(context, scanId).AsAsyncEnumerable()
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            counts.SawPhysicalObservation = true;
            var row = Project(file);
            if (row == null)
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            csv.WriteRecord(row);
            await csv.NextRecordAsync().ConfigureAwait(false);
            counts.Selected++;
            counts.UnknownClassification += row.ClassicPageType == null ? 1 : 0;
            counts.Poisoned += row.NoExecute == true ? 1 : 0;
            counts.PoisonUnknown += row.NoExecute == null ? 1 : 0;
            counts.NoExecuteMissing += string.IsNullOrEmpty(file.NoExecuteRaw) ? 1 : 0;
            var state = file.CoverageState ?? ClassicAspxCoverageStates.Unknown;
            counts.CoverageStates[state] = counts.CoverageStates.GetValueOrDefault(state) + 1;
        }
        return counts;
    }

    /// <summary>Hashes the single completed business CSV without creating compatibility aliases or a raw-file report.</summary>
    private static async Task<ExportFileEvidence> FileEvidenceAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new ExportFileEvidence(ClassicAspxCsv, stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>Counts only successfully written business rows; coverage labels remain observed evidence rather than tenant totals.</summary>
    private sealed class ExportCounts
    {
        internal bool SawPhysicalObservation { get; set; }
        internal long Selected { get; set; }
        internal long UnknownClassification { get; set; }
        internal long Poisoned { get; set; }
        internal long PoisonUnknown { get; set; }
        internal long NoExecuteMissing { get; set; }
        internal SortedDictionary<string, long> CoverageStates { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Immutable byte-level evidence for the completed canonical CSV.</summary>
    private sealed record ExportFileEvidence(string name, long length, string sha256);
}
