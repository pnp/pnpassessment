#nullable enable
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Discovery.Aspx;

namespace PnP.Scanning.Core.Storage;

/// <summary>SQL projections and ordered, typed streaming for the two approved ASPX native CSVs.</summary>
internal static class ClassicAspxReportProjection
{
    internal const string InventoryCsv = "classicaspxinventories.csv";
    internal const string BindingsCsv = "classicaspxitembindings.csv";
    internal const string InfoFile = "classicaspxinfo.txt";
    internal const string CanonicalInventoryCsv = "aspxinventory.csv";
    internal const string CanonicalBindingsCsv = "aspxitembindings.csv";
    internal const string SummaryJson = "aspxsummary.json";
    internal const string DeclaredScopeBindingCountBasis = "DeclaredScanScopeObserved";
    private const string SummarySchemaVersion = "aspx-inventory-summary/v1";
    private const string SummaryCountBasis = "Observed unique resource and normalized binding keys in the exported declared-scope streams";

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
        internal const string VersionedCatalogReference = "VersionedCatalogReference";
    }



    private static class ReportInfoText
    {
        internal const string NativePrefix = ClassicAspxProjectionContract.PropertyName + "=" +
            ClassicAspxProjectionContract.Version + "\nAcquisitionState=" + AcquisitionStates.DeclaredScopeObserved +
            "\n" + ClassicAspxProjectionContract.ScopeModePropertyName + "=";
        internal const string NativeScopeLimitations =
            "\nBinding counts describe stored distinct item/resource pairs, if any. " +
            "Zero bindings do not prove absence; consult ItemBindings collection state. " +
            "CoverageState Unknown/Incomplete/Changed is preserved in each inventory row. " +
            "HomePageOnly is targeted, not full inventory or tenant coverage.\n";
        internal const string MetadataOnlyItems =
            "ItemOnlyPages=NotCollected\nItemBindings=NotCollected\n" +
            "Display-form counts are metadata declarations, not rendered page or list-item counts.\n";
        internal const string ObservedWithoutMarker =
            "AspxProjectionContract=UnknownContract\nAcquisitionState=ObservedWithoutNativeMarker\nAspxScopeMode=Unknown\n" +
            "Normalized rows were observed without a native aspx-inventory/v1 marker, for example after resuming an older scan. " +
            "These observations are real but do not establish the aspx-inventory/v1 acquisition contract or complete coverage. " +
            "Counts remain declared-scope observations; migration itself never backfills facts.\n";
        internal const string Legacy =
            "AspxProjectionContract=NotAcquired/Legacy\nAcquisitionState=NotAcquired\nAspxScopeMode=Unknown\n" +
            "This scan has no native aspx-inventory/v1 acquisition marker. Empty upgraded ASPX tables are not proof that no ASPX exists. " +
            "Migration does not backfill historical inventory, bindings or Handler facts.\n";
        internal const string NullableEncodingPrefix = "AspxNullableStringEncoding=" +
            ClassicAspxNullableStringConverter.EncodingVersion + "; null=";
        internal const string NullableEncodingLimitations =
            "; non-null leading backslash is escaped with one extra backslash; empty string stays empty.\n";
    }

    /// <summary>Builds the ordered SQL projection with distinct normalized item/resource counts, including inventories with no observed binding.</summary>
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
                   BindingCountBasis = DeclaredScopeBindingCountBasis
               };
    }

    /// <summary>Builds a no-tracking, scan-scoped binding query in deterministic normalized-key order without materializing it.</summary>
    internal static IQueryable<ClassicAspxItemBinding> Bindings(ScanContext context, Guid scanId) =>
        context.ClassicAspxItemBindings.AsNoTracking().Where(row => row.ScanId == scanId)
            .OrderBy(row => row.AspxKey).ThenBy(row => row.SiteCollectionId).ThenBy(row => row.WebId)
            .ThenBy(row => row.ListId).ThenBy(row => row.ItemId);

    /// <summary>Projects ordered bindings to typed CSV rows in SQL while retaining nullable supplementary item GUIDs.</summary>
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

    /// <summary>Streams each typed projection once, counts the exported observations, and writes explicit acquisition and scope limitations.</summary>
    internal static async Task ExportCsvAsync(ScanContext context, Guid scanId, string exportPath,
        CsvConfiguration configuration, CancellationToken cancellationToken = default)
    {
        long zeroObservedBindingsCount = 0;
        var observedCoverageStates = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var observedInventoryCount = await WriteTypedCsvAsync(Path.Combine(exportPath, InventoryCsv), configuration,
            Inventories(context, scanId).AsAsyncEnumerable(), cancellationToken, row =>
            {
                if (row.DistinctItemBindingCount == 0)
                {
                    zeroObservedBindingsCount++;
                }
                var state = row.CoverageState ?? ClassicAspxCoverageStates.Unknown;
                observedCoverageStates[state] = observedCoverageStates.GetValueOrDefault(state) + 1;
            }).ConfigureAwait(false);
        var observedDistinctBindingCount = await WriteTypedCsvAsync(Path.Combine(exportPath, BindingsCsv), configuration,
            BindingRowsForExport(context, scanId).AsAsyncEnumerable(), cancellationToken).ConfigureAwait(false);

        var storedContract = await ClassicAspxProjectionContract.ReadAsync(context, scanId, cancellationToken)
            .ConfigureAwait(false);
        var contract = storedContract.Version;
        var scope = storedContract.ScopeMode;
        var metadataOnlyItems = await context.ClassicPageDiscoveries.AsNoTracking().AnyAsync(row =>
            row.ScanId == scanId && row.RowType == "Scope" &&
            row.ScopeType == ClassicAspxAcquisitionScopes.ItemEnumerationPolicy &&
            row.DiscoveryStatus == "PolicyExcluded", cancellationToken).ConfigureAwait(false);
        var observedWithoutMarker = contract != ClassicAspxProjectionContract.Version &&
            (await context.ClassicAspxInventories.AsNoTracking().AnyAsync(row => row.ScanId == scanId, cancellationToken).ConfigureAwait(false) ||
             await context.ClassicAspxItemBindings.AsNoTracking().AnyAsync(row => row.ScanId == scanId, cancellationToken).ConfigureAwait(false));
        var info = contract == ClassicAspxProjectionContract.Version
            ? ReportInfoText.NativePrefix + (scope ?? SummaryStates.Unknown) + ReportInfoText.NativeScopeLimitations
            : observedWithoutMarker
                ? ReportInfoText.ObservedWithoutMarker
                : ReportInfoText.Legacy;
        if (metadataOnlyItems) info += ReportInfoText.MetadataOnlyItems;
        info += ReportInfoText.NullableEncodingPrefix + ClassicAspxNullableStringConverter.NullToken +
            ReportInfoText.NullableEncodingLimitations;
        await File.WriteAllTextAsync(Path.Combine(exportPath, InfoFile), info, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        await WriteCanonicalSummaryAsync(context, scanId, exportPath, contract, scope,
            observedInventoryCount,
            observedDistinctBindingCount,
            zeroObservedBindingsCount,
            observedCoverageStates, metadataOnlyItems, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies completed CSV streams to canonical aliases and writes hashed file evidence with observed-scope counts, not tenant completeness.</summary>
    private static async Task WriteCanonicalSummaryAsync(ScanContext context, Guid scanId, string exportPath,
        string? contract, string? scope, long observedInventoryCount, long observedDistinctBindingCount,
        long zeroObservedBindingsCount, IReadOnlyDictionary<string, long> observedCoverageStates,
        bool metadataOnlyItems, CancellationToken cancellationToken)
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
        var catalogState = SummaryStates.Unknown;
        string? catalogSource = null;
        string? catalogVersion = null;
        string? catalogDigest = null;
        if (!string.IsNullOrEmpty(catalogPayload))
        {
            try
            {
                var catalog = PlatformAspxCatalog.FromFrozenPayload(catalogPayload);
                catalogState = SummaryStates.VersionedCatalogReference;
                catalogSource = catalog.Source;
                catalogVersion = catalog.Version;
                catalogDigest = catalog.Digest;
            }
            catch (Exception error) when (error is ArgumentException or FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                catalogState = SummaryStates.Unknown; // Invalid/missing evidence is not a claim of no platform ASPX.
            }
        }
        var acquisitionState = contract == ClassicAspxProjectionContract.Version ? AcquisitionStates.DeclaredScopeObserved :
            observedInventoryCount != 0 || observedDistinctBindingCount != 0 ? AcquisitionStates.ObservedWithoutNativeMarker : AcquisitionStates.NotAcquiredLegacy;
        var listDisplayFormReferences = await context.ClassicPageDiscoveries.AsNoTracking().LongCountAsync(row =>
            row.ScanId == scanId && row.RowType == "Reference" &&
            row.ScopeType == AspxReferenceSourceKinds.ListDefaultDisplayForm, cancellationToken).ConfigureAwait(false);
        var contentTypeDisplayFormReferences = await context.ClassicPageDiscoveries.AsNoTracking().LongCountAsync(row =>
            row.ScanId == scanId && row.RowType == "Reference" &&
            row.ScopeType == AspxReferenceSourceKinds.ContentTypeDisplayForm, cancellationToken).ConfigureAwait(false);
        var summary = new
        {
            schemaVersion = SummarySchemaVersion,
            scanId,
            projectionContract = contract,
            acquisitionState,
            scopeMode = scope ?? SummaryStates.Unknown,
            templateExclusions = new
            {
                state = exclusions == null ? SummaryStates.Unknown : SummaryStates.FrozenPolicy,
                value = exclusions
            },
            catalog = new
            {
                state = catalogState,
                source = catalogSource,
                version = catalogVersion,
                digest = catalogDigest
            },
            observedInventoryCount,
            observedDistinctBindingCount,
            zeroObservedBindingsCount,
            itemOnlyPageCollectionState = metadataOnlyItems ? "NotCollected" : SummaryStates.Unknown,
            itemBindingCollectionState = metadataOnlyItems ? "NotCollected" : SummaryStates.Unknown,
            displayFormDeclarations = new
            {
                listDefaultReferenceCount = listDisplayFormReferences,
                contentTypeReferenceCount = contentTypeDisplayFormReferences,
                countBasis = "Nonempty URLs declared in list and list content-type metadata; not item or rendered-page counts"
            },
            countBasis = SummaryCountBasis,
            coverage = new
            {
                targeted = scope == ClassicAspxProjectionContract.HomePageOnly,
                tenantCompleteness = SummaryStates.NotAsserted,
                inventoryStates = observedCoverageStates
            },
            nullableStringEncoding = ClassicAspxNullableStringConverter.EncodingVersion,
            files = new[] { inventoryFile, bindingsFile }
        };
        await File.WriteAllTextAsync(Path.Combine(exportPath, SummaryJson), JsonSerializer.Serialize(summary,
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies one completed compatibility CSV verbatim and returns its canonical name, byte length, and SHA256 evidence.</summary>
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

    /// <summary>Writes an explicit typed header even for empty input, streams rows with cancellation, and counts only successfully written records.</summary>
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
            count++;
            observed?.Invoke(row);
        }
        return count;
    }
}
