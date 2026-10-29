#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Discovery.Aspx;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

public sealed class ClassicAspxReportExportTests : IClassFixture<ScanContextFixture>
{
    private readonly ScanContextFixture fixture;
    /// <summary>
    /// Uses the shared SQLite fixture while each export scenario isolates its rows by a new scan ID.
    /// </summary>
    public ClassicAspxReportExportTests(ScanContextFixture fixture) => this.fixture = fixture;
    /// <summary>
    /// Uses invariant comma-delimited CSV configuration so export assertions do not depend on the machine locale.
    /// </summary>
    private static CsvConfiguration Configuration() => new(CultureInfo.InvariantCulture) { Delimiter = "," };
    /// <summary>
    /// Creates a uniquely named temporary output directory owned by one report scenario.
    /// </summary>
    private static string DirectoryForTest() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aspx-csv-" + Guid.NewGuid().ToString("N"))).FullName;
    /// <summary>
    /// Deletes only an output directory guarded by the temporary root and the report-test ownership prefix.
    /// </summary>
    private static void DeleteOwnDirectory(string directory)
    {
        var full = Path.GetFullPath(directory); var temporary = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("aspx-csv-", StringComparison.Ordinal))
            throw new InvalidOperationException("Not an owned report-test directory.");
        Directory.Delete(full, true);
    }
    /// <summary>
    /// Materializes typed CSV records before disposing both readers, using the same invariant configuration as export.
    /// </summary>
    private static List<T> Read<T>(string file)
    {
        using var reader = new StreamReader(file); using var csv = new CsvReader(reader, Configuration());
        return csv.GetRecords<T>().ToList();
    }
    /// <summary>
    /// Reads only the CSV header to verify typed empty exports and their published columns.
    /// </summary>
    private static string[] Header(string file)
    {
        using var reader = new StreamReader(file); using var csv = new CsvReader(reader, Configuration());
        csv.Read(); csv.ReadHeader(); return csv.HeaderRecord!;
    }
    /// <summary>
    /// Builds a tenant-file row with optional stable physical scope, unknown coverage, and explicit observation timestamps.
    /// </summary>
    private static ClassicAspxInventory Inventory(Guid scan, Guid? site = null, Guid? file = null)
    {
        var sc = site ?? Guid.NewGuid(); var id = file ?? Guid.NewGuid(); var now = DateTime.UtcNow;
        return new ClassicAspxInventory { ScanId = scan, AspxKey = ClassicAspxInventory.CreateTenantFileKey(sc, id), IdentityKind = ClassicAspxIdentityKinds.TenantFile,
            SiteCollectionId = sc, FileUniqueId = id, WebId = Guid.NewGuid(), Url = "/sites/test/renderer.aspx", FileName = "renderer.aspx",
            FileReadState = "Read", HandlerReadState = "Read", HandlerParseState = "Parsed", CoverageState = "Unknown",
            FirstObservedAtUtc = now, ObservedAtUtc = now, ProvenanceJson = "[]" };
    }
    /// <summary>
    /// Builds an observed renderer pair with caller-selected item scope and an unavailable supplementary GUID.
    /// </summary>
    private static ClassicAspxItemBinding Binding(ClassicAspxInventory inventory, Guid web, Guid list, int item) => new()
    {
        ScanId = inventory.ScanId, AspxKey = inventory.AspxKey, SiteCollectionId = inventory.SiteCollectionId!.Value,
        WebId = web, ListId = list, ItemId = item, ItemGuid = null, ItemGuidReadState = "Unavailable",
        BindingKinds = ClassicAspxBindingKinds.ListFormRenderer, BindingProvenanceJson = "[]", ObservedAtUtc = DateTime.UtcNow
    };
    /// <summary>
    /// Stores the native projection marker and declared scan scope as independent golden wire values for report verification.
    /// </summary>
    private static void Mark(ScanContext context, Guid scan, string scope = "DeclaredFullInventory") => context.Properties.AddRange(
        new Property { ScanId = scan, Name = "AspxProjectionContract", Value = "aspx-inventory/v1" },
        new Property { ScanId = scan, Name = "AspxScopeMode", Value = scope });

    /// <summary>Exports earlier saved full and targeted scans with neutral labels while retaining their stored facts and declaration bytes.</summary>
    [Theory]
    [InlineData("DeclaredFullInventory")]
    [InlineData("HomePageOnly")]
    public async Task EarlierSavedDeclarationsExportNeutralLabelsWithoutBackfill(string scope)
    {
        var scan = Guid.NewGuid();
        var inventory = Inventory(scan);
        using var context = fixture.CreateContext();
        context.Add(inventory);
        context.Properties.AddRange(
            new Property { ScanId = scan, Name = "M1ProjectionContract", Value = "M1/v1" },
            new Property { ScanId = scan, Name = "M1ScopeMode", Value = scope });
        await context.SaveChangesAsync();
        var before = await context.Properties.AsNoTracking().Where(row => row.ScanId == scan)
            .OrderBy(row => row.Name).Select(row => new { row.Name, row.Value }).ToListAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var info = await File.ReadAllTextAsync(Path.Combine(directory, ClassicAspxReportProjection.InfoFile));
            info.Should().Contain("AspxProjectionContract=aspx-inventory/v1")
                .And.Contain("AspxScopeMode=" + scope).And.NotContain("M1");
            var summaryText = await File.ReadAllTextAsync(Path.Combine(directory, ClassicAspxReportProjection.SummaryJson));
            summaryText.Should().NotContain("M1");
            using var summary = JsonDocument.Parse(summaryText);
            summary.RootElement.GetProperty("projectionContract").GetString()
                .Should().Be("aspx-inventory/v1");
            Read<ClassicAspxInventoryExport>(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv))
                .Should().ContainSingle();
            var after = await context.Properties.AsNoTracking().Where(row => row.ScanId == scan)
                .OrderBy(row => row.Name).Select(row => new { row.Name, row.Value }).ToListAsync();
            after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>
    /// Requires SQL-side left-joined counting and CSV export to retain all one hundred bindings with nullable supplementary GUIDs.
    /// </summary>
    [Fact]
    public async Task SqlGroupCountUsesLeftJoinAndKeepsOneHundredNullableGuidBindings()
    {
        var scan = Guid.NewGuid(); var inventory = Inventory(scan); var web = Guid.NewGuid(); var list = Guid.NewGuid();
        using (var seed = fixture.CreateContext()) { seed.Add(inventory); seed.AddRange(Enumerable.Range(1, 100).Select(i => Binding(inventory, web, list, i))); await seed.SaveChangesAsync(); }
        using var context = fixture.CreateContext(); var query = ClassicAspxReportProjection.Inventories(context, scan);
        var sql = query.ToQueryString(); sql.Should().Contain("LEFT JOIN").And.Contain("COUNT(");
        var row = await query.SingleAsync(); row.DistinctItemBindingCount.Should().Be(100); row.CoverageState.Should().Be("Unknown");
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            Read<ClassicAspxInventoryExport>(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)).Single().DistinctItemBindingCount.Should().Be(100);
            Read<ClassicAspxItemBinding>(Path.Combine(directory, ClassicAspxReportProjection.BindingsCsv)).Should().HaveCount(100);
        }
        finally { DeleteOwnDirectory(directory); }
    }

    /// <summary>
    /// Writes two source reasons through the actual projection writer and verifies they export as one pair counted once.
    /// </summary>
    [Fact]
    public async Task ActualWriterTwoSourceReasonsExportOneNormalizedPairAndCountOne()
    {
        var scan = Guid.NewGuid(); var inventory = Inventory(scan); var web = Guid.NewGuid(); var list = Guid.NewGuid();
        var writer = new ClassicAspxProjectionWriter(fixture.CreateContext);
        var first = Binding(inventory, web, list, 1); first.BindingProvenanceJson = "[{\"source\":\"ListForm\"}]";
        await writer.WriteAsync(new[] { inventory }, new[] { first });
        var second = Binding(inventory, web, list, 1); second.BindingKinds = ClassicAspxBindingKinds.ContentTypeRenderer;
        second.BindingProvenanceJson = "[{\"source\":\"ContentType\"}]";
        await writer.WriteAsync(Array.Empty<ClassicAspxInventory>(), new[] { second });
        using var context = fixture.CreateContext();
        (await ClassicAspxReportProjection.Inventories(context, scan).SingleAsync()).DistinctItemBindingCount.Should().Be(1);
        var row = await ClassicAspxReportProjection.Bindings(context, scan).SingleAsync();
        row.BindingKinds.Should().Be(ClassicAspxBindingKinds.ListFormRenderer | ClassicAspxBindingKinds.ContentTypeRenderer);
        row.BindingProvenanceJson.Should().Contain("ListForm").And.Contain("ContentType");
    }

    /// <summary>
    /// Keeps unbound inventory with a zero observed count while preserving its original coverage state and declared-scope count basis.
    /// </summary>
    [Theory]
    [InlineData("Unknown")]
    [InlineData("Incomplete")]
    [InlineData("Changed")]
    public async Task UnboundInventoryIsKeptWithZeroObservedCountWithoutPromotingCoverage(string coverage)
    {
        var scan = Guid.NewGuid(); var inventory = Inventory(scan); inventory.CoverageState = coverage;
        using (var seed = fixture.CreateContext()) { seed.Add(inventory); await seed.SaveChangesAsync(); }
        using var context = fixture.CreateContext();
        var row = await ClassicAspxReportProjection.Inventories(context, scan).SingleAsync();
        row.DistinctItemBindingCount.Should().Be(0); row.CoverageState.Should().Be(coverage); row.BindingCountBasis.Should().Be("DeclaredScanScopeObserved");
    }

    /// <summary>
    /// Counts real scoped pairs per renderer and scan without collapsing equal integer IDs across webs or lists.
    /// </summary>
    [Fact]
    public async Task ScopeDifferencesAndMultipleRenderersDoNotCollapseObservedPairCounts()
    {
        var scan = Guid.NewGuid(); var inventory = Inventory(scan); var secondInventory = Inventory(scan, inventory.SiteCollectionId); var web = Guid.NewGuid(); var list = Guid.NewGuid();
        using (var seed = fixture.CreateContext())
        {
            seed.AddRange(inventory, secondInventory);
            seed.AddRange(Binding(inventory, web, list, 1), Binding(inventory, Guid.NewGuid(), list, 1), Binding(inventory, web, Guid.NewGuid(), 1), Binding(secondInventory, web, list, 1));
            var otherScanInventory = Inventory(Guid.NewGuid(), inventory.SiteCollectionId, inventory.FileUniqueId); seed.Add(otherScanInventory);
            seed.Add(Binding(otherScanInventory, web, list, 1)); await seed.SaveChangesAsync();
        }
        using var context = fixture.CreateContext(); var rows = await ClassicAspxReportProjection.Inventories(context, scan).ToListAsync();
        rows.Should().HaveCount(2); rows.Single(x => x.AspxKey == inventory.AspxKey).DistinctItemBindingCount.Should().Be(3);
        rows.Single(x => x.AspxKey == secondInventory.AspxKey).DistinctItemBindingCount.Should().Be(1);
        (await ClassicAspxReportProjection.Bindings(context, scan).CountAsync()).Should().Be(4);
    }

    /// <summary>
    /// Round-trips Unicode, quoting, multiline reasons, nullable handlers, and provenance through the typed CSV contract.
    /// </summary>
    [Fact]
    public async Task TypedCsvPreservesUnicodeEscapingNullableHandlersAndProvenance()
    {
        var scan = Guid.NewGuid(); var inventory = Inventory(scan);
        inventory.Url = "/sites/test/測試,\"renderer\".aspx"; inventory.HandlerDeclaredType = null; inventory.HandlerEffectiveType = null;
        inventory.HandlerSource = "FileBody"; inventory.HandlerDefaultProvenance = "known-family-default"; inventory.HandlerReasonCode = "body,unavailable\nsecond-line";
        inventory.ProvenanceJson = "[{\"source\":\"Unicode測試,quoted\"}]"; inventory.CoverageState = "Incomplete";
        using (var seed = fixture.CreateContext()) { seed.Add(inventory); Mark(seed, scan); await seed.SaveChangesAsync(); }
        var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext(); await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var row = Read<ClassicAspxInventoryExport>(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)).Single();
            row.Url.Should().Be(inventory.Url); row.HandlerDeclaredType.Should().BeNull(); row.HandlerEffectiveType.Should().BeNull();
            row.HandlerSource.Should().Be("FileBody"); row.HandlerDefaultProvenance.Should().Be("known-family-default"); row.HandlerReasonCode.Should().Be(inventory.HandlerReasonCode);
            row.ProvenanceJson.Should().Be(inventory.ProvenanceJson); row.CoverageState.Should().Be("Incomplete");
            Header(Path.Combine(directory, ClassicAspxReportProjection.BindingsCsv)).Should().Contain("BindingKinds").And.Contain("ItemGuidReadState");
            Header(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)).Should().Contain("DistinctItemBindingCount").And.Contain("CatalogDigest").And.Contain("FirstObservedAtUtc");
        }
        finally { DeleteOwnDirectory(directory); }
    }

    /// <summary>
    /// Exports an unbound frozen-catalog reference once, without tenant identifiers, invented handlers, or source payload columns.
    /// </summary>
    [Fact]
    public async Task FrozenCatalogRowsExportOnceWithoutWebMultiplicationOrPayloadHandlerClaim()
    {
        var scan = Guid.NewGuid(); var digest = new string('a', 64);
        var inventory = new ClassicAspxInventory { ScanId = scan, AspxKey = ClassicAspxInventory.CreatePlatformKey("catalog", "v1", digest, "entry"), IdentityKind = ClassicAspxIdentityKinds.PlatformCatalog,
            CatalogSource = "catalog", CatalogVersion = "v1", CatalogDigest = digest, CatalogEntryKey = "entry", Url = "/_layouts/15/listform.aspx",
            HandlerSource = "VersionedCatalogReference", HandlerEffectiveType = null, FileReadState = "NotRequested", HandlerReadState = "NotRequested", CoverageState = "Unknown" };
        using (var seed = fixture.CreateContext()) { seed.Add(inventory); Mark(seed, scan); await seed.SaveChangesAsync(); }
        var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext(); await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var row = Read<ClassicAspxInventoryExport>(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)).Single();
            row.SiteCollectionId.Should().BeNull(); row.FileUniqueId.Should().BeNull(); row.CatalogDigest.Should().Be(digest); row.HandlerEffectiveType.Should().BeNull();
            row.DistinctItemBindingCount.Should().Be(0);
            Header(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)).Should().NotContain("Payload").And.NotContain("Body").And.NotContain("ContentBytes");
        }
        finally { DeleteOwnDirectory(directory); }
    }

    /// <summary>
    /// Reports newly observed rows from an unmarked resumed scan without claiming either no acquisition or a known native contract.
    /// </summary>
    [Fact]
    public async Task ResumedLegacyScanWithNewRowsIsObservedWithoutMarkerNotFalselyNotAcquired()
    {
        var scan = Guid.NewGuid(); var inventory = Inventory(scan);
        using (var seed = fixture.CreateContext()) { seed.Add(inventory); await seed.SaveChangesAsync(); }
        var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext(); await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            Read<ClassicAspxInventoryExport>(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)).Should().HaveCount(1);
            var info = await File.ReadAllTextAsync(Path.Combine(directory, ClassicAspxReportProjection.InfoFile));
            info.Should().Contain("AcquisitionState=ObservedWithoutNativeMarker").And.Contain("AspxProjectionContract=UnknownContract");
            info.Should().NotContain("AcquisitionState=NotAcquired").And.NotContain("AspxProjectionContract=aspx-inventory/v1");
        }
        finally { DeleteOwnDirectory(directory); }
    }

    /// <summary>
    /// Separates null, empty, sentinel, and leading-backslash values on the CSV wire while preserving typed fields and database contents.
    /// </summary>
    [Theory]
    [InlineData(null, @"\N")]
    [InlineData("", "")]
    [InlineData(@"\N", @"\\N")]
    [InlineData(@"\\N", @"\\\N")]
    [InlineData(@"\leading", @"\\leading")]
    [InlineData(@"inside\path", @"inside\path")]
    [InlineData("測試,\"quote\"\nline", "測試,\"quote\"\nline")]
    public async Task NullableStringsRoundTripNullEmptySentinelAndLeadingBackslashWithoutCollisions(string? raw, string wire)
    {
        ClassicAspxNullableStringConverter.Encode(raw).Should().Be(wire);
        ClassicAspxNullableStringConverter.Decode(wire).Should().Be(raw);
        var scan = Guid.NewGuid(); var inventory = Inventory(scan);
        inventory.HandlerDeclaredType = raw; inventory.HandlerNormalizedType = raw; inventory.HandlerEffectiveType = raw;
        inventory.HandlerDefaultProvenance = raw; inventory.HandlerSource = raw; inventory.HandlerReasonCode = raw;
        var binding = Binding(inventory, Guid.NewGuid(), Guid.NewGuid(), 1); binding.ItemTitle = raw; binding.ContentTypeId = raw;
        using (var seed = fixture.CreateContext()) { seed.Add(inventory); seed.Add(binding); Mark(seed, scan); await seed.SaveChangesAsync(); }
        var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext(); await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var projected = Read<ClassicAspxInventoryExport>(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)).Single();
            projected.HandlerDeclaredType.Should().Be(raw); projected.HandlerNormalizedType.Should().Be(raw); projected.HandlerEffectiveType.Should().Be(raw);
            projected.HandlerDefaultProvenance.Should().Be(raw); projected.HandlerSource.Should().Be(raw); projected.HandlerReasonCode.Should().Be(raw);
            var pair = Read<ClassicAspxItemBindingExport>(Path.Combine(directory, ClassicAspxReportProjection.BindingsCsv)).Single();
            pair.ItemTitle.Should().Be(raw); pair.ContentTypeId.Should().Be(raw);
            using (var reader = new StreamReader(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)))
            using (var csv = new CsvReader(reader, Configuration()))
            {
                csv.Read(); csv.ReadHeader(); csv.Read(); csv.GetField<string>("HandlerDeclaredType").Should().Be(wire);
            }
            (await context.ClassicAspxInventories.SingleAsync(x => x.ScanId == scan)).HandlerDeclaredType.Should().Be(raw);
            var info = await File.ReadAllTextAsync(Path.Combine(directory, ClassicAspxReportProjection.InfoFile));
            info.Should().Contain("AspxNullableStringEncoding=BackslashNullV1").And.Contain(@"null=\N");
        }
        finally { DeleteOwnDirectory(directory); }
    }

    /// <summary>
    /// Emits typed headers for empty ASPX tables and explains that legacy absence is not evidence that no ASPX exists.
    /// </summary>
    [Fact]
    public async Task EmptyNativeM1TablesHaveTypedHeadersAndLegacyNotAcquiredInfo()
    {
        var scan = Guid.NewGuid(); var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext(); await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            Header(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)).Should().Contain("AspxKey").And.Contain("DistinctItemBindingCount");
            Header(Path.Combine(directory, ClassicAspxReportProjection.BindingsCsv)).Should().Contain("AspxKey").And.Contain("ItemId");
            Read<ClassicAspxInventoryExport>(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)).Should().BeEmpty();
            var info = await File.ReadAllTextAsync(Path.Combine(directory, ClassicAspxReportProjection.InfoFile));
            info.Should().Contain("NotAcquired/Legacy").And.Contain("not proof that no ASPX exists");
        }
        finally { DeleteOwnDirectory(directory); }
    }

    /// <summary>
    /// Exercises native Classic report dispatch, preserving legacy CSV output while adding ASPX exports with targeted-scope metadata.
    /// </summary>
    [Fact]
    public async Task NativeClassicDispatchAddsTwoM1CsvsAndPreservesLegacyCsvs()
    {
        var scan = Guid.NewGuid(); var inventory = Inventory(scan);
        using (var seed = fixture.CreateContext())
        {
            seed.Add(inventory); Mark(seed, scan, "HomePageOnly");
            seed.ClassicPages.Add(new ClassicPage { ScanId = scan, SiteUrl = "https://example.invalid/sites/test", WebUrl = "/", PageUrl = "/Home.aspx", CustomizedPageStatus = "Uncustomized" });
            await seed.SaveChangesAsync();
        }
        var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext(); await ReportManager.ExportClassicReportDataAsync(context, scan, directory, Configuration());
            File.Exists(Path.Combine(directory, "discovery.csv")).Should().BeTrue();
            Read<ClassicPage>(Path.Combine(directory, "classicpages.csv")).Single().PageUrl.Should().Be("/Home.aspx");
            Read<ClassicAspxInventoryExport>(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)).Should().HaveCount(1);
            Header(Path.Combine(directory, ClassicAspxReportProjection.BindingsCsv)).Should().Contain("ItemId");
            var info = await File.ReadAllTextAsync(Path.Combine(directory, ClassicAspxReportProjection.InfoFile));
            info.Should().Contain("AspxProjectionContract=aspx-inventory/v1").And.Contain("AspxScopeMode=HomePageOnly").And.Contain("targeted, not full inventory");
        }
        finally { DeleteOwnDirectory(directory); }
    }

    /// <summary>
    /// Checks canonical CSV aliases byte-for-byte and validates versioned summary counts, coverage limits, lengths, and SHA-256 digests.
    /// </summary>
    [Fact]
    public async Task CanonicalAliasesPreserveCompatibilityBytesAndVersionedObservedSummaryCounts()
    {
        var scan = Guid.NewGuid(); var bound = Inventory(scan); var unbound = Inventory(scan);
        unbound.CoverageState = "Incomplete"; var web = Guid.NewGuid(); var list = Guid.NewGuid();
        using (var seed = fixture.CreateContext())
        {
            seed.AddRange(bound, unbound); seed.AddRange(Enumerable.Range(1, 3).Select(i => Binding(bound, web, list, i)));
            Mark(seed, scan); seed.Properties.Add(new Property { ScanId = scan, Name = "excludesitetemplates", Value = "none" });
            await seed.SaveChangesAsync();
        }
        var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext(); await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, ClassicAspxReportProjection.SummaryJson)));
            var summary = json.RootElement;
            summary.GetProperty("schemaVersion").GetString().Should().Be("aspx-inventory-summary/v1");
            summary.GetProperty("scanId").GetGuid().Should().Be(scan);
            summary.GetProperty("projectionContract").GetString().Should().Be("aspx-inventory/v1");
            summary.GetProperty("scopeMode").GetString().Should().Be("DeclaredFullInventory");
            summary.GetProperty("observedInventoryCount").GetInt64().Should().Be(2);
            summary.GetProperty("observedDistinctBindingCount").GetInt64().Should().Be(3);
            summary.GetProperty("zeroObservedBindingsCount").GetInt64().Should().Be(1);
            summary.GetProperty("coverage").GetProperty("tenantCompleteness").GetString().Should().Be("NotAsserted");
            summary.GetProperty("coverage").GetProperty("inventoryStates").GetProperty("Incomplete").GetInt64().Should().Be(1);
            foreach (var file in summary.GetProperty("files").EnumerateArray())
            {
                var canonical = await File.ReadAllBytesAsync(Path.Combine(directory, file.GetProperty("name").GetString()!));
                var compatibility = await File.ReadAllBytesAsync(Path.Combine(directory, file.GetProperty("compatibilityAlias").GetString()!));
                canonical.Should().Equal(compatibility);
                file.GetProperty("length").GetInt64().Should().Be(canonical.Length);
                file.GetProperty("sha256").GetString().Should().Be(Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant());
            }
            Read<ClassicAspxInventoryExport>(Path.Combine(directory, "aspxinventory.csv")).Should().HaveCount(2);
            Read<ClassicAspxItemBindingExport>(Path.Combine(directory, "aspxitembindings.csv")).Should().HaveCount(3);
        }
        finally { DeleteOwnDirectory(directory); }
    }

    [Fact]
    public async Task Metadata_only_report_counts_form_declarations_without_claiming_item_counts()
    {
        var scan = Guid.NewGuid();
        using (var seed = fixture.CreateContext())
        {
            Mark(seed, scan);
            seed.ClassicPageDiscoveries.AddRange(
                new ClassicPageDiscovery
                {
                    ScanId = scan, RecordKey = "policy", RowType = "Scope",
                    ScopeType = "ItemEnumerationPolicy", DiscoveryStatus = "PolicyExcluded",
                    SiteUrl = "https://example.invalid/sites/test", WebUrl = "/"
                },
                new ClassicPageDiscovery
                {
                    ScanId = scan, RecordKey = "list-form", RowType = "Reference",
                    ScopeType = AspxReferenceSourceKinds.ListDefaultDisplayForm,
                    DiscoveryStatus = "Complete", SiteUrl = "https://example.invalid/sites/test", WebUrl = "/"
                },
                new ClassicPageDiscovery
                {
                    ScanId = scan, RecordKey = "ct-form-1", RowType = "Reference",
                    ScopeType = AspxReferenceSourceKinds.ContentTypeDisplayForm,
                    DiscoveryStatus = "Complete", SiteUrl = "https://example.invalid/sites/test", WebUrl = "/"
                },
                new ClassicPageDiscovery
                {
                    ScanId = scan, RecordKey = "ct-form-2", RowType = "Reference",
                    ScopeType = AspxReferenceSourceKinds.ContentTypeDisplayForm,
                    DiscoveryStatus = "Complete", SiteUrl = "https://example.invalid/sites/test", WebUrl = "/"
                });
            await seed.SaveChangesAsync();
        }
        var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext();
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, ClassicAspxReportProjection.SummaryJson)));
            var summary = json.RootElement;
            summary.GetProperty("itemOnlyPageCollectionState").GetString().Should().Be("NotCollected");
            summary.GetProperty("itemBindingCollectionState").GetString().Should().Be("NotCollected");
            var forms = summary.GetProperty("displayFormDeclarations");
            forms.GetProperty("listDefaultReferenceCount").GetInt64().Should().Be(1);
            forms.GetProperty("contentTypeReferenceCount").GetInt64().Should().Be(2);
            summary.GetProperty("observedDistinctBindingCount").GetInt64().Should().Be(0);
            var info = await File.ReadAllTextAsync(Path.Combine(directory, ClassicAspxReportProjection.InfoFile));
            info.Should().Contain("ItemOnlyPages=NotCollected").And.Contain("ItemBindings=NotCollected");
        }
        finally { DeleteOwnDirectory(directory); }
    }

    /// <summary>
    /// Reports an unbound targeted catalog member with unknown coverage and catalog metadata only, never embedding the frozen payload.
    /// </summary>
    [Fact]
    public async Task TargetedCatalogZeroMembersSummaryKeepsUnknownAndOnlyCatalogMetadata()
    {
        var scan = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":\"platform-aspx-catalog/v1\",\"catalogVersion\":\"synthetic-v1\",\"source\":\"synthetic-test\",\"entries\":[{\"id\":\"one\",\"deploymentIdentity\":\"synthetic\",\"culture\":\"neutral\",\"path\":\"/_layouts/15/example.aspx\"}]}");
        var catalog = PlatformAspxCatalog.Parse(bytes);
        await new ClassicAspxProjectionWriter(fixture.CreateContext).SeedCatalogAsync(scan, catalog);
        using (var seed = fixture.CreateContext())
        {
            Mark(seed, scan, "HomePageOnly");
            seed.Properties.Add(new Property { ScanId = scan, Name = "excludesitetemplates", Value = "SPSPERS#*,APP#*" });
            seed.Properties.Add(new Property { ScanId = scan, Name = "platformaspxcatalogpayload", Value = catalog.FrozenPayload });
            await seed.SaveChangesAsync();
        }
        var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext(); await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, ClassicAspxReportProjection.SummaryJson)));
            var summary = json.RootElement;
            summary.GetProperty("scopeMode").GetString().Should().Be("HomePageOnly");
            summary.GetProperty("coverage").GetProperty("targeted").GetBoolean().Should().BeTrue();
            summary.GetProperty("coverage").GetProperty("tenantCompleteness").GetString().Should().Be("NotAsserted");
            summary.GetProperty("coverage").GetProperty("inventoryStates").GetProperty("Unknown").GetInt64().Should().Be(1);
            summary.GetProperty("observedInventoryCount").GetInt64().Should().Be(1);
            summary.GetProperty("observedDistinctBindingCount").GetInt64().Should().Be(0);
            summary.GetProperty("itemOnlyPageCollectionState").GetString().Should().Be("Unknown");
            summary.GetProperty("itemBindingCollectionState").GetString().Should().Be("Unknown");
            summary.GetProperty("zeroObservedBindingsCount").GetInt64().Should().Be(1);
            var reference = summary.GetProperty("catalog");
            reference.GetProperty("state").GetString().Should().Be("VersionedCatalogReference");
            reference.GetProperty("source").GetString().Should().Be(catalog.Source);
            reference.GetProperty("version").GetString().Should().Be(catalog.Version);
            reference.GetProperty("digest").GetString().Should().Be(catalog.Digest);
            summary.GetRawText().Should().NotContain(catalog.FrozenPayload);
        }
        finally { DeleteOwnDirectory(directory); }
    }

    /// <summary>
    /// Represents an empty legacy scan as unacquired with unknown scope and catalog state, not as tenant-wide completeness.
    /// </summary>
    [Fact]
    public async Task LegacyEmptyCanonicalSummaryIsNotAcquiredAndUnknownNotCompleteTenant()
    {
        var scan = Guid.NewGuid(); var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext(); await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, ClassicAspxReportProjection.SummaryJson)));
            var summary = json.RootElement;
            summary.GetProperty("schemaVersion").GetString().Should().Be("aspx-inventory-summary/v1");
            summary.GetProperty("projectionContract").ValueKind.Should().Be(JsonValueKind.Null);
            summary.GetProperty("acquisitionState").GetString().Should().Be("NotAcquired/Legacy");
            summary.GetProperty("scopeMode").GetString().Should().Be("Unknown");
            summary.GetProperty("catalog").GetProperty("state").GetString().Should().Be("Unknown");
            summary.GetProperty("templateExclusions").GetProperty("state").GetString().Should().Be("Unknown");
            summary.GetProperty("observedInventoryCount").GetInt64().Should().Be(0);
            summary.GetProperty("observedDistinctBindingCount").GetInt64().Should().Be(0);
            summary.GetProperty("coverage").GetProperty("tenantCompleteness").GetString().Should().Be("NotAsserted");
            Header(Path.Combine(directory, "aspxinventory.csv")).Should().Contain("AspxKey").And.Contain("DistinctItemBindingCount");
            Header(Path.Combine(directory, "aspxitembindings.csv")).Should().Contain("ItemId");
            Read<ClassicAspxInventoryExport>(Path.Combine(directory, "aspxinventory.csv")).Should().BeEmpty();
            (await File.ReadAllBytesAsync(Path.Combine(directory, "aspxinventory.csv"))).Should()
                .Equal(await File.ReadAllBytesAsync(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)));
        }
        finally { DeleteOwnDirectory(directory); }
    }

    /// <summary>
    /// Migrates legacy pages without inventing historical ASPX rows and preserves legacy export alongside empty typed ASPX output.
    /// </summary>
    [Fact]
    public async Task LegacyMigrationCreatesEmptyM1TablesWithoutHistoricalBackfillAndExportsHeaders()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        await using var context = new ScanContext(new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options);
        var migrations = context.Database.GetMigrations().ToArray(); var inventoryMigrationIndex = Array.FindIndex(migrations, x => x.EndsWith("_ClassicAspxInventoryBindings"));
        inventoryMigrationIndex.Should().BeGreaterThan(0); await context.GetService<IMigrator>().MigrateAsync(migrations[inventoryMigrationIndex - 1]);
        var scan = Guid.NewGuid(); context.ClassicPages.Add(new ClassicPage { ScanId = scan, SiteUrl = "https://example.invalid/sites/legacy", WebUrl = "/", PageUrl = "/old.aspx" });
        await context.SaveChangesAsync(); context.ChangeTracker.Clear(); await context.Database.MigrateAsync();
        (await context.ClassicAspxInventories.CountAsync()).Should().Be(0); (await context.ClassicAspxItemBindings.CountAsync()).Should().Be(0);
        var directory = DirectoryForTest();
        try
        {
            await ReportManager.ExportClassicReportDataAsync(context, scan, directory, Configuration());
            Read<ClassicPage>(Path.Combine(directory, "classicpages.csv")).Single().PageUrl.Should().Be("/old.aspx");
            Read<ClassicAspxInventoryExport>(Path.Combine(directory, ClassicAspxReportProjection.InventoryCsv)).Should().BeEmpty();
            (await File.ReadAllTextAsync(Path.Combine(directory, ClassicAspxReportProjection.InfoFile))).Should().Contain("NotAcquired/Legacy");
        }
        finally { DeleteOwnDirectory(directory); }
    }
}
