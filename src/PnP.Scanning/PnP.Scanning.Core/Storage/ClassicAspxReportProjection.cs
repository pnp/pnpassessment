#nullable enable
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using PnP.Scanning.Core.Discovery.M1;

namespace PnP.Scanning.Core.Storage;

/// <summary>SQL projections and ordered, typed streaming for the two approved M1 native CSVs.</summary>
internal static class ClassicAspxReportProjection
{
    internal const string InventoryCsv = "classicaspxinventories.csv";
    internal const string BindingsCsv = "classicaspxitembindings.csv";
    internal const string InfoFile = "classicaspxinfo.txt";
    internal const string CanonicalInventoryCsv = "aspxinventory.csv";
    internal const string CanonicalBindingsCsv = "aspxitembindings.csv";
    internal const string SummaryJson = "aspxsummary.json";

    internal static IQueryable<ClassicAspxInventoryExport> Inventories(ScanContext context, Guid scanId)
    {
        // Binding PK is the normalized scoped item/resource pair, not (pair, source kind).
        // COUNT(*) on that constrained relation is a distinct pair count and must not count nullable ItemGuid.
        var counts = context.ClassicAspxItemBindings.AsNoTracking()
            .Where(binding => binding.ScanId == scanId)
            .GroupBy(binding => new { binding.ScanId, binding.AspxKey })
            .Select(group => new { group.Key.ScanId, group.Key.AspxKey, Count = (long?)group.LongCount() });

        return from inventory in context.ClassicAspxInventories.AsNoTracking().Where(row => row.ScanId == scanId)
               join count in counts on new { inventory.ScanId, inventory.AspxKey } equals new { count.ScanId, count.AspxKey } into matched
               from count in matched.DefaultIfEmpty()
               orderby inventory.IdentityKind, inventory.AspxKey
               select new ClassicAspxInventoryExport
               {
                   ScanId = inventory.ScanId,
                   AspxKey = inventory.AspxKey,
                   IdentityKind = inventory.IdentityKind,
                   IdentityVersion = inventory.IdentityVersion,
                   SiteCollectionId = inventory.SiteCollectionId,
                   FileUniqueId = inventory.FileUniqueId,
                   WebId = inventory.WebId,
                   SiteUrl = inventory.SiteUrl,
                   WebUrl = inventory.WebUrl,
                   Url = inventory.Url,
                   FileName = inventory.FileName,
                   PageType = inventory.PageType,
                   ContentTypeId = inventory.ContentTypeId,
                   CustomizedPageStatus = inventory.CustomizedPageStatus,
                   SetupPath = inventory.SetupPath,
                   FileReadState = inventory.FileReadState,
                   FileErrorCodes = inventory.FileErrorCodes,
                   FileErrorDetail = inventory.FileErrorDetail,
                   HandlerDeclaredType = inventory.HandlerDeclaredType,
                   HandlerNormalizedType = inventory.HandlerNormalizedType,
                   HandlerEffectiveType = inventory.HandlerEffectiveType,
                   HandlerReadState = inventory.HandlerReadState,
                   HandlerParseState = inventory.HandlerParseState,
                   HandlerSource = inventory.HandlerSource,
                   HandlerDefaultProvenance = inventory.HandlerDefaultProvenance,
                   HandlerReasonCode = inventory.HandlerReasonCode,
                   HandlerErrorCodes = inventory.HandlerErrorCodes,
                   HandlerErrorDetail = inventory.HandlerErrorDetail,
                   CatalogSource = inventory.CatalogSource,
                   CatalogVersion = inventory.CatalogVersion,
                   CatalogDigest = inventory.CatalogDigest,
                   CatalogEntryKey = inventory.CatalogEntryKey,
                   ProvenanceJson = inventory.ProvenanceJson,
                   CoverageState = inventory.CoverageState,
                   FirstObservedAtUtc = inventory.FirstObservedAtUtc,
                   ObservedAtUtc = inventory.ObservedAtUtc,
                   DistinctItemBindingCount = count.Count ?? 0L,
                   BindingCountBasis = "DeclaredScanScopeObserved"
               };
    }

    internal static IQueryable<ClassicAspxItemBinding> Bindings(ScanContext context, Guid scanId) =>
        context.ClassicAspxItemBindings.AsNoTracking().Where(row => row.ScanId == scanId)
            .OrderBy(row => row.AspxKey).ThenBy(row => row.SiteCollectionId).ThenBy(row => row.WebId)
            .ThenBy(row => row.ListId).ThenBy(row => row.ItemId);

    internal static IQueryable<ClassicAspxItemBindingExport> BindingRowsForExport(ScanContext context, Guid scanId) =>
        Bindings(context, scanId).Select(binding => new ClassicAspxItemBindingExport
        {
            ScanId = binding.ScanId,
            AspxKey = binding.AspxKey,
            SiteCollectionId = binding.SiteCollectionId,
            WebId = binding.WebId,
            ListId = binding.ListId,
            ItemId = binding.ItemId,
            ItemGuid = binding.ItemGuid,
            ItemGuidReadState = binding.ItemGuidReadState,
            SiteUrl = binding.SiteUrl,
            WebUrl = binding.WebUrl,
            ItemUrl = binding.ItemUrl,
            ItemTitle = binding.ItemTitle,
            ContentTypeId = binding.ContentTypeId,
            BindingKinds = binding.BindingKinds,
            BindingProvenanceJson = binding.BindingProvenanceJson,
            ObservedAtUtc = binding.ObservedAtUtc,
        });

    internal static async Task ExportCsvAsync(ScanContext context, Guid scanId, string exportPath,
        CsvConfiguration configuration, CancellationToken cancellationToken = default)
    {
        long zeroObservedBindingsCount = 0;
        var observedCoverageStates = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var observedInventoryCount = await WriteTypedCsvAsync(Path.Combine(exportPath, InventoryCsv), configuration,
            Inventories(context, scanId).AsAsyncEnumerable(), cancellationToken, row =>
            {
                if (row.DistinctItemBindingCount == 0) zeroObservedBindingsCount++;
                var state = row.CoverageState ?? "Unknown";
                observedCoverageStates[state] = observedCoverageStates.GetValueOrDefault(state) + 1;
            }).ConfigureAwait(false);
        var observedDistinctBindingCount = await WriteTypedCsvAsync(Path.Combine(exportPath, BindingsCsv), configuration,
            BindingRowsForExport(context, scanId).AsAsyncEnumerable(), cancellationToken).ConfigureAwait(false);

        var contract = await context.Properties.AsNoTracking()
            .Where(row => row.ScanId == scanId && row.Name == "M1ProjectionContract")
            .Select(row => row.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var scope = await context.Properties.AsNoTracking()
            .Where(row => row.ScanId == scanId && row.Name == "M1ScopeMode")
            .Select(row => row.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var observedWithoutMarker = contract != "M1/v1" &&
            (await context.ClassicAspxInventories.AsNoTracking().AnyAsync(row => row.ScanId == scanId, cancellationToken).ConfigureAwait(false) ||
             await context.ClassicAspxItemBindings.AsNoTracking().AnyAsync(row => row.ScanId == scanId, cancellationToken).ConfigureAwait(false));
        var info = contract == "M1/v1"
            ? "M1ProjectionContract=M1/v1\nAcquisitionState=DeclaredScopeObserved\nM1ScopeMode=" + (scope ?? "Unknown") +
              "\nCounts are observed distinct normalized item/resource pairs in this declared scan scope. " +
              "Zero bindings do not prove complete absence; CoverageState Unknown/Incomplete/Changed is preserved in each inventory row. " +
              "HomePageOnly is targeted, not full inventory or tenant coverage.\n"
            : observedWithoutMarker
            ? "M1ProjectionContract=UnknownContract\nAcquisitionState=ObservedWithoutNativeMarker\nM1ScopeMode=Unknown\n" +
              "Normalized rows were observed without a native M1/v1 marker, for example after resuming an older scan. " +
              "These observations are real but do not establish the M1/v1 acquisition contract or complete coverage. " +
              "Counts remain declared-scope observations; migration itself never backfills facts.\n"
            : "M1ProjectionContract=NotAcquired/Legacy\nAcquisitionState=NotAcquired\nM1ScopeMode=Unknown\n" +
              "This scan has no native M1/v1 acquisition marker. Empty upgraded M1 tables are not proof that no ASPX exists. " +
              "Migration does not backfill historical inventory, bindings or Handler facts.\n";
        info += "M1NullableStringEncoding=BackslashNullV1; null=" + ClassicAspxNullableStringConverter.NullToken +
            "; non-null leading backslash is escaped with one extra backslash; empty string stays empty.\n";
        await File.WriteAllTextAsync(Path.Combine(exportPath, InfoFile), info, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        await WriteCanonicalSummaryAsync(context, scanId, exportPath, contract, scope,
            observedInventoryCount, observedDistinctBindingCount, zeroObservedBindingsCount,
            observedCoverageStates, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteCanonicalSummaryAsync(ScanContext context, Guid scanId, string exportPath,
        string? contract, string? scope, long observedInventoryCount, long observedDistinctBindingCount,
        long zeroObservedBindingsCount, IReadOnlyDictionary<string, long> observedCoverageStates,
        CancellationToken cancellationToken)
    {
        // Canonical files are byte aliases of the completed typed streams, never a second database read.
        var inventoryFile = await CopyCanonicalAsync(exportPath, InventoryCsv, CanonicalInventoryCsv, cancellationToken).ConfigureAwait(false);
        var bindingsFile = await CopyCanonicalAsync(exportPath, BindingsCsv, CanonicalBindingsCsv, cancellationToken).ConfigureAwait(false);
        var exclusions = await context.Properties.AsNoTracking()
            .Where(row => row.ScanId == scanId && row.Name == Constants.StartExcludeSiteTemplates)
            .Select(row => row.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var catalogPayload = await context.Properties.AsNoTracking()
            .Where(row => row.ScanId == scanId && row.Name == Constants.StartPlatformAspxCatalogPayload)
            .Select(row => row.Value).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var catalogState = "Unknown"; string? catalogSource = null; string? catalogVersion = null; string? catalogDigest = null;
        if (!string.IsNullOrEmpty(catalogPayload))
        {
            try
            {
                var catalog = PlatformAspxCatalog.FromFrozenPayload(catalogPayload);
                catalogState = "VersionedCatalogReference"; catalogSource = catalog.Source;
                catalogVersion = catalog.Version; catalogDigest = catalog.Digest;
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                catalogState = "Unknown"; // Invalid/missing evidence is not a claim of no platform ASPX.
            }
        }
        var acquisitionState = contract == "M1/v1" ? "DeclaredScopeObserved" :
            observedInventoryCount != 0 || observedDistinctBindingCount != 0 ? "ObservedWithoutNativeMarker" : "NotAcquired/Legacy";
        var summary = new
        {
            schemaVersion = "aspx-inventory-summary/v1", scanId,
            projectionContract = contract, acquisitionState, scopeMode = scope ?? "Unknown",
            templateExclusions = new { state = exclusions == null ? "Unknown" : "FrozenPolicy", value = exclusions },
            catalog = new { state = catalogState, source = catalogSource, version = catalogVersion, digest = catalogDigest },
            observedInventoryCount, observedDistinctBindingCount, zeroObservedBindingsCount,
            countBasis = "Observed unique resource and normalized binding keys in the exported declared-scope streams",
            coverage = new { targeted = scope == "HomePageOnly", tenantCompleteness = "NotAsserted", inventoryStates = observedCoverageStates },
            nullableStringEncoding = "BackslashNullV1",
            files = new[] { inventoryFile, bindingsFile }
        };
        await File.WriteAllTextAsync(Path.Combine(exportPath, SummaryJson), JsonSerializer.Serialize(summary,
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CanonicalFileEvidence> CopyCanonicalAsync(string exportPath, string compatibilityName,
        string canonicalName, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(exportPath, canonicalName);
        File.Copy(Path.Combine(exportPath, compatibilityName), destination, true);
        await using var stream = File.OpenRead(destination);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new CanonicalFileEvidence(canonicalName, compatibilityName, stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private sealed record CanonicalFileEvidence(string name, string compatibilityAlias, long length, string sha256);

    private static async Task<long> WriteTypedCsvAsync<T>(string path, CsvConfiguration configuration,
        IAsyncEnumerable<T> rows, CancellationToken cancellationToken, Action<T>? observed = null)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        using var csv = new CsvWriter(writer, configuration);
        // Explicit typed header makes an empty native table a header-only CSV, not a missing schema.
        csv.WriteHeader<T>();
        await csv.NextRecordAsync().ConfigureAwait(false);
        long count = 0;
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            csv.WriteRecord(row);
            await csv.NextRecordAsync().ConfigureAwait(false);
            count++; observed?.Invoke(row);
        }
        return count;
    }
}
