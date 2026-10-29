#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PnP.Scanning.Core.Discovery.Aspx;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

public sealed class ClassicAspxReportExportTests : IClassFixture<ScanContextFixture>
{
    private static readonly string[] ExpectedHeader =
    {
        "ScanId", "AspxKey", "SiteCollectionId", "WebId", "ListId", "FolderUniqueId", "FileUniqueId",
        "SiteUrl", "WebUrl", "Url", "FileName", "ContentTypeId", "CustomizedPageStatus", "SetupPath",
        "FileReadState", "FileErrorCodes", "FileErrorDetail", "ProvenanceJson", "CoverageState",
        "FirstObservedAtUtc", "ObservedAtUtc"
    };
    private readonly ScanContextFixture fixture;

    /// <summary>Uses the shared migrated SQLite fixture while each export scenario isolates its rows by a new assessment ID.</summary>
    public ClassicAspxReportExportTests(ScanContextFixture fixture)
    {
        this.fixture = fixture;
    }

    /// <summary>Uses invariant comma-delimited CSV so wire expectations do not depend on the machine locale.</summary>
    private static CsvConfiguration Configuration() => new(CultureInfo.InvariantCulture) { Delimiter = "," };

    /// <summary>Creates a unique output directory whose ownership prefix is checked before cleanup.</summary>
    private static string DirectoryForTest() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "aspx-csv-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Deletes only an output directory contained by the temporary root and marked with this report-test ownership prefix.</summary>
    private static void DeleteOwnDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("aspx-csv-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Not an owned report-test directory.");
        }
        Directory.Delete(full, true);
    }

    /// <summary>Materializes typed inventory records before disposing both readers, using the same invariant configuration as export.</summary>
    private static List<ClassicAspxInventoryExport> ReadInventory(string directory)
    {
        using var reader = new StreamReader(Path.Combine(directory, "aspxinventory.csv"));
        using var csv = new CsvReader(reader, Configuration());
        return csv.GetRecords<ClassicAspxInventoryExport>().ToList();
    }

    /// <summary>Reads the canonical CSV header independently of the export type so column names and order remain a golden wire contract.</summary>
    private static string[] Header(string directory)
    {
        using var reader = new StreamReader(Path.Combine(directory, "aspxinventory.csv"));
        using var csv = new CsvReader(reader, Configuration());
        csv.Read();
        csv.ReadHeader();
        return csv.HeaderRecord!;
    }

    /// <summary>Creates one real physical-file observation with optional owner metadata and explicit observed coverage.</summary>
    private static ClassicAspxInventory Inventory(Guid scan, Guid? site = null, Guid? file = null)
    {
        var sc = site ?? Guid.NewGuid();
        var id = file ?? Guid.NewGuid();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new ClassicAspxInventory
        {
            ScanId = scan,
            AspxKey = ClassicAspxInventory.CreateTenantFileKey(sc, id),
            SiteCollectionId = sc,
            FileUniqueId = id,
            WebId = Guid.NewGuid(),
            ListId = Guid.NewGuid(),
            FolderUniqueId = Guid.NewGuid(),
            Url = "/sites/test/renderer.aspx",
            FileName = "renderer.aspx",
            FileReadState = "Available",
            CoverageState = "Observed",
            FirstObservedAtUtc = now,
            ObservedAtUtc = now,
            ProvenanceJson = "[]"
        };
    }

    /// <summary>Stores current projection and scope markers as independent golden wire values rather than production constants.</summary>
    private static void Mark(ScanContext context, Guid scan, string scope = "DeclaredFullInventory") =>
        context.Properties.AddRange(
            new Property { ScanId = scan, Name = "AspxProjectionContract", Value = "aspx-inventory/v1" },
            new Property { ScanId = scan, Name = "AspxScopeMode", Value = scope });

    /// <summary>Requires a scan-scoped ordered single-table SQL projection without obsolete joins, item counts, or in-memory schema shaping.</summary>
    [Fact]
    public async Task SqlProjectionUsesOnlyInventoryInDeterministicKeyOrder()
    {
        var scan = Guid.NewGuid();
        var first = Inventory(scan);
        var second = Inventory(scan, first.SiteCollectionId);
        using var context = fixture.CreateContext();
        context.AddRange(first, second, Inventory(Guid.NewGuid(), first.SiteCollectionId, first.FileUniqueId));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var query = ClassicAspxReportProjection.Inventories(context, scan);
        var sql = query.ToQueryString();
        sql.Should().Contain("ORDER BY").And.Contain("ClassicAspxInventories")
            .And.NotContain("JOIN").And.NotContain("GROUP BY").And.NotContain("COUNT(")
            .And.NotContain("ClassicAspxItemBindings");
        var rows = await query.ToListAsync();
        rows.Select(row => row.AspxKey).Should().Equal(new[] { first.AspxKey, second.AspxKey }.OrderBy(key => key, StringComparer.Ordinal));
        rows.Should().HaveCount(2).And.OnlyContain(row => row.ScanId == scan);
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    /// <summary>Publishes exactly one canonical 21-column CSV and one summary, without binding files, aliases, or information-text sidecars.</summary>
    [Fact]
    public async Task DirectExportPublishesOnlyCanonicalInventoryAndSummary()
    {
        var scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.Add(Inventory(scan));
        Mark(context, scan);
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            Directory.GetFiles(directory).Select(Path.GetFileName).Should().BeEquivalentTo("aspxinventory.csv", "aspxsummary.json");
            Header(directory).Should().Equal(ExpectedHeader);
            ReadInventory(directory).Should().ContainSingle();
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Exports multiple observation sources for a real file as one inventory row whose provenance retains all enrichment roles.</summary>
    [Fact]
    public async Task ActualWriterUnionExportsOneFileAndAllObservationProvenance()
    {
        var scan = Guid.NewGuid();
        var first = Inventory(scan);
        first.ProvenanceJson = "[{\"source\":\"FolderFileObservation\"}]";
        var next = Inventory(scan, first.SiteCollectionId, first.FileUniqueId);
        next.WebId = first.WebId;
        next.ListId = first.ListId;
        next.FolderUniqueId = first.FolderUniqueId;
        next.ProvenanceJson = "[{\"role\":\"ListDefaultDisplayForm\"}]";
        var writer = new ClassicAspxProjectionWriter(fixture.CreateContext);
        await writer.WriteAsync(new[] { first });
        await writer.WriteAsync(new[] { next });
        var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext();
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var row = ReadInventory(directory).Should().ContainSingle().Which;
            row.AspxKey.Should().Be(first.AspxKey);
            row.ProvenanceJson.Should().Contain("FolderFileObservation").And.Contain("ListDefaultDisplayForm");
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Retains per-file coverage labels in both CSV and summary without promoting any observed scope to tenant-wide completeness.</summary>
    [Theory]
    [InlineData("Unknown")]
    [InlineData("Observed")]
    [InlineData("Incomplete")]
    [InlineData("ChangedDuringScan")]
    public async Task CoverageRemainsObservedEvidenceNotTenantCompleteness(string coverage)
    {
        var scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        var inventory = Inventory(scan);
        inventory.CoverageState = coverage;
        context.Add(inventory);
        Mark(context, scan);
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            ReadInventory(directory).Single().CoverageState.Should().Be(coverage);
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "aspxsummary.json")));
            summary.RootElement.GetProperty("coverage").GetProperty("inventoryStates").GetProperty(coverage).GetInt64().Should().Be(1);
            summary.RootElement.GetProperty("coverage").GetProperty("tenantCompleteness").GetString().Should().Be("NotAsserted");
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Round-trips Unicode, quoting, multiline error evidence, nullable metadata, real IDs, and timestamps through the typed 21-column CSV.</summary>
    [Fact]
    public async Task TypedCsvPreservesAllPhysicalObservationFacts()
    {
        var scan = Guid.NewGuid();
        var inventory = Inventory(scan);
        inventory.Url = "/sites/test/測試,\"renderer\".aspx";
        inventory.SiteUrl = "https://example.invalid/sites/test";
        inventory.WebUrl = "/";
        inventory.ContentTypeId = null;
        inventory.CustomizedPageStatus = string.Empty;
        inventory.SetupPath = "features/template.aspx";
        inventory.FileErrorCodes = "HTTP403";
        inventory.FileErrorDetail = "metadata,unavailable\nsecond-line";
        inventory.ProvenanceJson = "[{\"source\":\"Unicode測試,quoted\"}]";
        using var context = fixture.CreateContext();
        context.Add(inventory);
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var row = ReadInventory(directory).Single();
            row.Should().BeEquivalentTo(inventory);
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Separates null, empty, sentinel, and leading-backslash strings on the wire and preserves every nullable inventory text field.</summary>
    [Theory]
    [InlineData(null, @"\N")]
    [InlineData("", "")]
    [InlineData(@"\N", @"\\N")]
    [InlineData(@"\\N", @"\\\N")]
    [InlineData(@"\leading", @"\\leading")]
    [InlineData(@"inside\path", @"inside\path")]
    [InlineData("測試,\"quote\"\nline", "測試,\"quote\"\nline")]
    public async Task NullableTextEncodingIsReversibleAndDeclaredInSummary(string? raw, string wire)
    {
        ClassicAspxNullableStringConverter.Encode(raw).Should().Be(wire);
        ClassicAspxNullableStringConverter.Decode(wire).Should().Be(raw);
        var scan = Guid.NewGuid();
        var inventory = Inventory(scan);
        inventory.SiteUrl = raw;
        inventory.WebUrl = raw;
        inventory.Url = raw;
        inventory.FileName = raw;
        inventory.ContentTypeId = raw;
        inventory.CustomizedPageStatus = raw;
        inventory.SetupPath = raw;
        inventory.FileErrorCodes = raw;
        inventory.FileErrorDetail = raw;
        using var context = fixture.CreateContext();
        context.Add(inventory);
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            ReadInventory(directory).Single().Should().BeEquivalentTo(inventory);
            using (var reader = new StreamReader(Path.Combine(directory, "aspxinventory.csv")))
            using (var csv = new CsvReader(reader, Configuration()))
            {
                csv.Read();
                csv.ReadHeader();
                csv.Read();
                csv.GetField<string>("SiteUrl").Should().Be(wire);
            }
            (await context.ClassicAspxInventories.AsNoTracking().SingleAsync(row => row.ScanId == scan)).SiteUrl.Should().Be(raw);
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "aspxsummary.json")));
            summary.RootElement.GetProperty("nullableStringEncoding").GetString().Should().Be("BackslashNullV1");
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Emits the full typed header for an empty upgraded assessment while reporting absence of acquisition authority, not absence of ASPX.</summary>
    [Fact]
    public async Task EmptyExportHasTypedHeaderAndDoesNotClaimAcquisition()
    {
        var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext();
            await ClassicAspxReportProjection.ExportCsvAsync(context, Guid.NewGuid(), directory, Configuration());
            Header(directory).Should().Equal(ExpectedHeader);
            ReadInventory(directory).Should().BeEmpty();
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "aspxsummary.json")));
            var root = summary.RootElement;
            root.GetProperty("projectionContract").ValueKind.Should().Be(JsonValueKind.Null);
            root.GetProperty("acquisitionState").GetString().Should().Be("NotAcquired/Legacy");
            root.GetProperty("scopeMode").GetString().Should().Be("Unknown");
            root.GetProperty("observedInventoryCount").GetInt64().Should().Be(0);
            root.GetProperty("coverage").GetProperty("tenantCompleteness").GetString().Should().Be("NotAsserted");
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Reports real observations in an unmarked assessment without relabeling them unacquired or asserting a known native contract.</summary>
    [Fact]
    public async Task UnmarkedObservedRowsRetainUnknownContract()
    {
        var scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.Add(Inventory(scan));
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "aspxsummary.json")));
            summary.RootElement.GetProperty("projectionContract").ValueKind.Should().Be(JsonValueKind.Null);
            summary.RootElement.GetProperty("acquisitionState").GetString().Should().Be("ObservedWithoutNativeMarker");
            summary.RootElement.GetProperty("observedInventoryCount").GetInt64().Should().Be(1);
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Preserves the current full/home declaration as read-only scope evidence and explicitly marks home acquisition as targeted.</summary>
    [Theory]
    [InlineData("DeclaredFullInventory", false)]
    [InlineData("HomePageOnly", true)]
    public async Task CurrentScopeExportsWithoutMutationOrCoveragePromotion(string scope, bool targeted)
    {
        var scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.Add(Inventory(scan));
        Mark(context, scan, scope);
        await context.SaveChangesAsync();
        var before = await context.Properties.AsNoTracking().Where(row => row.ScanId == scan)
            .OrderBy(row => row.Name).Select(row => new { row.Name, row.Value }).ToListAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "aspxsummary.json")));
            var root = summary.RootElement;
            root.GetProperty("projectionContract").GetString().Should().Be("aspx-inventory/v1");
            root.GetProperty("scopeMode").GetString().Should().Be(scope);
            root.GetProperty("acquisitionState").GetString().Should().Be("DeclaredScopeObserved");
            root.GetProperty("coverage").GetProperty("targeted").GetBoolean().Should().Be(targeted);
            root.GetProperty("coverage").GetProperty("tenantCompleteness").GetString().Should().Be("NotAsserted");
            var after = await context.Properties.AsNoTracking().Where(row => row.ScanId == scan)
                .OrderBy(row => row.Name).Select(row => new { row.Name, row.Value }).ToListAsync();
            after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Validates summary counts, frozen exclusions, coverage totals, and SHA256 evidence for the single completed CSV stream.</summary>
    [Fact]
    public async Task SummaryDescribesOnlyTheCompletedInventoryStream()
    {
        var scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        var first = Inventory(scan);
        var second = Inventory(scan);
        second.CoverageState = "Incomplete";
        context.AddRange(first, second);
        Mark(context, scan);
        context.Properties.Add(new Property { ScanId = scan, Name = "excludesitetemplates", Value = "none" });
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var text = await File.ReadAllTextAsync(Path.Combine(directory, "aspxsummary.json"));
            text.Should().NotContain("Binding").And.NotContain("binding").And.NotContain("catalog")
                .And.NotContain("Handler").And.NotContain("NotCollected").And.NotContain("compatibilityAlias");
            using var summary = JsonDocument.Parse(text);
            var root = summary.RootElement;
            root.GetProperty("schemaVersion").GetString().Should().Be("aspx-inventory-summary/v1");
            root.GetProperty("scanId").GetGuid().Should().Be(scan);
            root.GetProperty("observedInventoryCount").GetInt64().Should().Be(2);
            root.GetProperty("templateExclusions").GetProperty("state").GetString().Should().Be("FrozenPolicy");
            root.GetProperty("templateExclusions").GetProperty("value").GetString().Should().Be("none");
            root.GetProperty("coverage").GetProperty("inventoryStates").GetProperty("Observed").GetInt64().Should().Be(1);
            root.GetProperty("coverage").GetProperty("inventoryStates").GetProperty("Incomplete").GetInt64().Should().Be(1);
            var evidence = root.GetProperty("files").EnumerateArray().Should().ContainSingle().Which;
            evidence.GetProperty("name").GetString().Should().Be("aspxinventory.csv");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "aspxinventory.csv"));
            evidence.GetProperty("length").GetInt64().Should().Be(bytes.Length);
            evidence.GetProperty("sha256").GetString().Should().Be(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Never turns declaration-only raw references into inventory rows or reintroduces binding collection state in the report.</summary>
    [Fact]
    public async Task MetadataReferencesDoNotInventInventoryOrBindingPlaceholders()
    {
        var scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        Mark(context, scan);
        context.ClassicPageDiscoveries.Add(new ClassicPageDiscovery
        {
            ScanId = scan,
            RecordKey = "metadata-only-reference",
            RowType = "Reference",
            ScopeType = "ListDefaultDisplayForm",
            DiscoveryStatus = "Complete",
            SiteUrl = "https://example.invalid/sites/test",
            WebUrl = "/",
            Url = "/sites/test/Forms/DispForm.aspx"
        });
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            ReadInventory(directory).Should().BeEmpty();
            var text = await File.ReadAllTextAsync(Path.Combine(directory, "aspxsummary.json"));
            using var summary = JsonDocument.Parse(text);
            summary.RootElement.GetProperty("observedInventoryCount").GetInt64().Should().Be(0);
            text.Should().NotContain("NotCollected").And.NotContain("Binding").And.NotContain("binding");
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Exercises the actual Classic report dispatcher while retaining existing discovery and page CSVs alongside only the canonical ASPX outputs.</summary>
    [Fact]
    public async Task NativeClassicDispatchPreservesDiscoveryAndLegacyCsvs()
    {
        var scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.Add(Inventory(scan));
        Mark(context, scan, "HomePageOnly");
        context.ClassicPages.Add(new ClassicPage
        {
            ScanId = scan, SiteUrl = "https://example.invalid/sites/test", WebUrl = "/",
            PageUrl = "/Home.aspx", CustomizedPageStatus = "Uncustomized"
        });
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ReportManager.ExportClassicReportDataAsync(context, scan, directory, Configuration());
            File.Exists(Path.Combine(directory, "discovery.csv")).Should().BeTrue();
            using var reader = new StreamReader(Path.Combine(directory, "classicpages.csv"));
            using var csv = new CsvReader(reader, Configuration());
            csv.GetRecords<ClassicPage>().Single().PageUrl.Should().Be("/Home.aspx");
            ReadInventory(directory).Should().ContainSingle();
            var files = Directory.GetFiles(directory).Select(Path.GetFileName).ToArray();
            files.Should().Contain("aspxinventory.csv").And.Contain("aspxsummary.json")
                .And.NotContain("classicaspxinventories.csv").And.NotContain("classicaspxitembindings.csv")
                .And.NotContain("aspxitembindings.csv").And.NotContain("classicaspxinfo.txt");
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Runs the real main-to-inventory-only migration path and exports legacy pages without inventing historical physical observations.</summary>
    [Fact]
    public async Task MainMigrationPreservesLegacyExportWithoutBackfill()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new ScanContext(new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options);
        var migrations = context.Database.GetMigrations().ToArray();
        migrations.Last().Should().EndWith("_ClassicAspxInventoryOnly");
        await context.GetService<IMigrator>().MigrateAsync(migrations[^2]);
        var scan = Guid.NewGuid();
        context.ClassicPages.Add(new ClassicPage
        {
            ScanId = scan, SiteUrl = "https://example.invalid/sites/legacy", WebUrl = "/", PageUrl = "/old.aspx"
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await context.Database.MigrateAsync();
        (await context.ClassicAspxInventories.CountAsync()).Should().Be(0);
        var directory = DirectoryForTest();
        try
        {
            await ReportManager.ExportClassicReportDataAsync(context, scan, directory, Configuration());
            using var reader = new StreamReader(Path.Combine(directory, "classicpages.csv"));
            using var csv = new CsvReader(reader, Configuration());
            csv.GetRecords<ClassicPage>().Single().PageUrl.Should().Be("/old.aspx");
            Header(directory).Should().Equal(ExpectedHeader);
            ReadInventory(directory).Should().BeEmpty();
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "aspxsummary.json")));
            summary.RootElement.GetProperty("acquisitionState").GetString().Should().Be("NotAcquired/Legacy");
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }
}
