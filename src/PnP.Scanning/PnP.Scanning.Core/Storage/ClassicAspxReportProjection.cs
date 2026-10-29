#nullable enable
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PnP.Scanning.Core.Storage;

/// <summary>One ordered SQL inventory projection and its canonical CSV and observed-scope summary.</summary>
internal static class ClassicAspxReportProjection
{
    internal const string InventoryCsv = "aspxinventory.csv";
    internal const string SummaryJson = "aspxsummary.json";
    private const string SummarySchemaVersion = "aspx-inventory-summary/v1";
    private const string SummaryCountBasis = "Observed unique physical ASPX files in the exported declared-scope stream";

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

    /// <summary>Projects exactly the canonical 21 fields in SQL, scoped to one assessment and ordered by its physical-file key.</summary>
    internal static IQueryable<ClassicAspxInventoryExport> Inventories(ScanContext context, Guid scanId) =>
        context.ClassicAspxInventories.AsNoTracking()
            .Where(inventory => inventory.ScanId == scanId)
            .OrderBy(inventory => inventory.AspxKey)
            .Select(inventory => new ClassicAspxInventoryExport
            {
                ScanId = inventory.ScanId,
                AspxKey = inventory.AspxKey,
                SiteCollectionId = inventory.SiteCollectionId,
                WebId = inventory.WebId,
                ListId = inventory.ListId,
                FolderUniqueId = inventory.FolderUniqueId,
                FileUniqueId = inventory.FileUniqueId,
                SiteUrl = inventory.SiteUrl,
                WebUrl = inventory.WebUrl,
                Url = inventory.Url,
                FileName = inventory.FileName,
                ContentTypeId = inventory.ContentTypeId,
                CustomizedPageStatus = inventory.CustomizedPageStatus,
                SetupPath = inventory.SetupPath,
                FileReadState = inventory.FileReadState,
                FileErrorCodes = inventory.FileErrorCodes,
                FileErrorDetail = inventory.FileErrorDetail,
                ProvenanceJson = inventory.ProvenanceJson,
                CoverageState = inventory.CoverageState,
                FirstObservedAtUtc = inventory.FirstObservedAtUtc,
                ObservedAtUtc = inventory.ObservedAtUtc
            });

    /// <summary>Streams inventory once, counts the written rows and coverage labels, then publishes only the canonical CSV and summary.</summary>
    internal static async Task ExportCsvAsync(ScanContext context, Guid scanId, string exportPath,
        CsvConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var observedCoverageStates = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var path = Path.Combine(exportPath, InventoryCsv);
        var observedInventoryCount = await WriteTypedCsvAsync(path, configuration,
            Inventories(context, scanId).AsAsyncEnumerable(), cancellationToken, row =>
            {
                var state = row.CoverageState ?? ClassicAspxCoverageStates.Unknown;
                observedCoverageStates[state] = observedCoverageStates.GetValueOrDefault(state) + 1;
            }).ConfigureAwait(false);
        var declaration = await ClassicAspxProjectionContract.ReadAsync(context, scanId, cancellationToken).ConfigureAwait(false);
        var exclusions = await context.Properties.AsNoTracking()
            .Where(row => row.ScanId == scanId && row.Name == Constants.StartExcludeSiteTemplates)
            .Select(row => row.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var acquisitionState = declaration.IsRecognized
            ? AcquisitionStates.DeclaredScopeObserved
            : observedInventoryCount != 0 ? AcquisitionStates.ObservedWithoutNativeMarker : AcquisitionStates.NotAcquiredLegacy;
        var file = await FileEvidenceAsync(path, cancellationToken).ConfigureAwait(false);
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
            observedInventoryCount,
            countBasis = SummaryCountBasis,
            coverage = new
            {
                targeted = declaration.ScopeMode == ClassicAspxProjectionContract.HomePageOnly,
                tenantCompleteness = SummaryStates.NotAsserted,
                inventoryStates = observedCoverageStates
            },
            nullableStringEncoding = ClassicAspxNullableStringConverter.EncodingVersion,
            files = new[] { file }
        };
        await File.WriteAllTextAsync(Path.Combine(exportPath, SummaryJson), JsonSerializer.Serialize(summary,
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Hashes the completed canonical inventory stream and reports its filename and byte length without compatibility copies.</summary>
    private static async Task<InventoryFileEvidence> FileEvidenceAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new InventoryFileEvidence(InventoryCsv, stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>Immutable evidence for the single completed inventory CSV stream.</summary>
    private sealed record InventoryFileEvidence(string name, long length, string sha256);

    /// <summary>Writes the typed header even for empty input, streams cancellable rows, and counts only successfully written records.</summary>
    private static async Task<long> WriteTypedCsvAsync(string path, CsvConfiguration configuration,
        IAsyncEnumerable<ClassicAspxInventoryExport> rows, CancellationToken cancellationToken,
        Action<ClassicAspxInventoryExport> observed)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        using var csv = new CsvWriter(writer, configuration);
        csv.WriteHeader<ClassicAspxInventoryExport>();
        await csv.NextRecordAsync().ConfigureAwait(false);
        long count = 0;
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            csv.WriteRecord(row);
            await csv.NextRecordAsync().ConfigureAwait(false);
            count++;
            observed(row);
        }
        return count;
    }
}
