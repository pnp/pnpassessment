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
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

/// <summary>Exercises the canonical business CSV through real SQLite facts and the native Classic report dispatcher.</summary>
public sealed class ClassicAspxReportExportTests
{
    private const string CsvName = "classicaspx.csv";
    private const string SummaryName = "classicaspxsummary.json";
    private const string ModernApplication = "B6917CB1-93A0-4B97-A84D-7CF49975D4EC";
    private const string ModernContentType = "0x0101009D1CB255DA76424F860D91F20E6C411800";
    private static readonly string[] ExpectedHeader =
    {
        "ScanId", "SiteCollectionId", "WebId", "ListId", "FileUniqueId", "SiteUrl", "WebUrl", "Url", "FileName",
        "ClassicPageType", "PageRole", "ClassificationState", "ClassificationReason", "ContentTypeId",
        "CustomizedPageStatus", "SetupPath", "NoExecuteRaw", "IsPoisoned", "PoisonState", "RenderDisposition",
        "MetadataReadState", "ErrorCodes", "ErrorDetail", "ObservedAtUtc"
    };

    /// <summary>Uses invariant comma-delimited CSV for independent golden wire assertions.</summary>
    private static CsvConfiguration Configuration() => new(CultureInfo.InvariantCulture) { Delimiter = "," };

    /// <summary>Creates a unique output directory whose absolute ownership boundary is checked before cleanup.</summary>
    private static string DirectoryForTest() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "classic-aspx-csv-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Deletes only this test's resolved absolute directory beneath the temporary root and with the expected ownership prefix.</summary>
    private static void DeleteOwnDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("classic-aspx-csv-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Not an owned report-test directory.");
        }
        Directory.Delete(full, true);
    }

    /// <summary>Materializes typed business records while both CSV readers remain owned by this helper.</summary>
    private static List<ClassicAspxExport> ReadRows(string directory)
    {
        using var reader = new StreamReader(Path.Combine(directory, CsvName));
        using var csv = new CsvReader(reader, Configuration());
        return csv.GetRecords<ClassicAspxExport>().ToList();
    }

    /// <summary>Reads the header independently of the DTO so order and column names are a fixed golden contract.</summary>
    private static string[] Header(string directory)
    {
        using var reader = new StreamReader(Path.Combine(directory, CsvName));
        using var csv = new CsvReader(reader, Configuration());
        csv.Read();
        csv.ReadHeader();
        return csv.HeaderRecord!;
    }

    /// <summary>Creates one physical file fact with real GUID identity and no fabricated item or NoExecute observation.</summary>
    private static SPFile FileFact(Guid scan, Guid? site = null, Guid? file = null)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new SPFile
        {
            ScanId = scan,
            SiteCollectionId = site ?? Guid.NewGuid(),
            FileUniqueId = file ?? Guid.NewGuid(),
            WebId = Guid.NewGuid(),
            ListId = Guid.NewGuid(),
            FolderUniqueId = Guid.NewGuid(),
            SiteUrl = "https://example.invalid/sites/test",
            WebUrl = "https://example.invalid/sites/test",
            ServerRelativeUrl = "/sites/test/renderer.aspx",
            FileName = "renderer.aspx",
            FileReadState = "Available",
            CoverageState = "Observed",
            FirstObservedAtUtc = now,
            ObservedAtUtc = now,
            ProvenanceJson = "[]"
        };
    }

    /// <summary>Stores current acquisition/scope markers as independent wire values, not export-production constants.</summary>
    private static void Mark(ScanContext context, Guid scan, string scope = "DeclaredFullInventory") =>
        context.Properties.AddRange(
            new Property { ScanId = scan, Name = "AspxProjectionContract", Value = "aspx-inventory/v1" },
            new Property { ScanId = scan, Name = "AspxScopeMode", Value = scope });

    /// <summary>Uses only SPFile and native GUID ordering in SQL; extension filtering and existing classification remain outside the raw-fact query.</summary>
    [Fact]
    public async Task SqlProjectionUsesOnlyFactsInDeterministicNativeKeyOrder()
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        var first = FileFact(scan);
        var second = FileFact(scan, first.SiteCollectionId);
        var otherScan = FileFact(Guid.NewGuid());
        using var context = fixture.CreateContext();
        context.AddRange(first, second, otherScan);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var query = ClassicAspxReportProjection.Files(context, scan);
        var sql = query.ToQueryString();
        sql.Should().Contain("ORDER BY").And.Contain("SPFile").And.Contain("SiteCollectionId").And.Contain("FileUniqueId")
            .And.NotContain("JOIN").And.NotContain("GROUP BY").And.NotContain("COUNT(").And.NotContain("IsClassic");
        var rows = await query.ToListAsync();
        rows.Select(row => (row.SiteCollectionId, row.FileUniqueId)).Should().Equal(
            new[] { first, second }.OrderBy(row => row.SiteCollectionId).ThenBy(row => row.FileUniqueId)
                .Select(row => (row.SiteCollectionId, row.FileUniqueId)));
        rows.Should().HaveCount(2).And.OnlyContain(row => row.ScanId == scan);
        context.ChangeTracker.Entries().Should().BeEmpty();
    }

    /// <summary>Isolates scans with the same native pair in separate real databases instead of violating the new two-column key in one fixture.</summary>
    [Fact]
    public async Task SameNativePairAcrossAssessmentsUsesIsolatedFixtures()
    {
        using var firstFixture = new ScanContextFixture();
        using var secondFixture = new ScanContextFixture();
        var site = Guid.NewGuid();
        var file = Guid.NewGuid();
        var first = FileFact(Guid.NewGuid(), site, file);
        var second = FileFact(Guid.NewGuid(), site, file);
        using var firstContext = firstFixture.CreateContext();
        using var secondContext = secondFixture.CreateContext();
        firstContext.Add(first);
        secondContext.Add(second);
        await firstContext.SaveChangesAsync();
        await secondContext.SaveChangesAsync();
        (await ClassicAspxReportProjection.Files(firstContext, first.ScanId).SingleAsync()).ScanId.Should().Be(first.ScanId);
        (await ClassicAspxReportProjection.Files(secondContext, second.ScanId).SingleAsync()).ScanId.Should().Be(second.ScanId);
    }

    /// <summary>Publishes exactly the fixed 24-column business CSV and its summary, without inventory, raw-file or alias outputs.</summary>
    [Fact]
    public async Task DirectExportPublishesOnlyCanonicalBusinessFiles()
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.Add(FileFact(scan));
        Mark(context, scan);
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            Directory.GetFiles(directory).Select(Path.GetFileName).Should().BeEquivalentTo(CsvName, SummaryName);
            Header(directory).Should().Equal(ExpectedHeader).And.HaveCount(24);
            ReadRows(directory).Should().ContainSingle();
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Excludes only existing modern results and non-ASPX physical files, preserving failed, unknown and file-only classic rows without an assessment join.</summary>
    [Fact]
    public async Task BusinessFilterPreservesUnknownAndFailedPhysicalRows()
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        var classic = FileFact(scan);
        classic.ItemMetadataReadState = "Available";
        classic.WikiFieldHasValue = true;
        classic.ItemMetadataFieldStatesJson = "{\"WikiField\":\"value\"}";
        var unknown = FileFact(scan);
        unknown.FileReadState = "Unavailable";
        unknown.FileErrorCodes = "HTTP403";
        var fallback = FileFact(scan);
        fallback.ContentTypeId = "0x010109";
        var modern = FileFact(scan);
        modern.ItemMetadataReadState = "Available";
        modern.ClientSideApplicationId = ModernApplication;
        modern.ItemMetadataFieldStatesJson = "{\"ClientSideApplicationId\":\"value\"}";
        var modernCt = FileFact(scan);
        modernCt.ContentTypeId = ModernContentType;
        var nonAspx = FileFact(scan);
        nonAspx.FileName = "file.txt";
        nonAspx.ServerRelativeUrl = "/sites/test/file.txt";
        using var context = fixture.CreateContext();
        context.AddRange(classic, unknown, fallback, modern, modernCt, nonAspx);
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var rows = ReadRows(directory);
            rows.Select(row => row.FileUniqueId).Should().BeEquivalentTo(new[] { classic.FileUniqueId, unknown.FileUniqueId, fallback.FileUniqueId });
            rows.Single(row => row.FileUniqueId == classic.FileUniqueId).ClassicPageType.Should().Be("WikiPage");
            rows.Single(row => row.FileUniqueId == fallback.FileUniqueId).ClassicPageType.Should().Be("BasicPage");
            var failure = rows.Single(row => row.FileUniqueId == unknown.FileUniqueId);
            failure.ClassicPageType.Should().BeNull();
            failure.ClassificationState.Should().Be("Unknown");
            failure.ErrorCodes.Should().Be("HTTP403");
            (await context.SPFiles.CountAsync()).Should().Be(6);
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Streams one row for merged observations of one real pair and derives its role only through the existing setup-path classifier.</summary>
    [Fact]
    public async Task ActualWriterUnionExportsOnePhysicalFile()
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        var first = FileFact(scan);
        first.ProvenanceJson = "[{\"source\":\"FolderFileObservation\"}]";
        first.SetupPath = @"1033\templates\pages\form.aspx";
        var next = FileFact(scan, first.SiteCollectionId, first.FileUniqueId);
        next.WebId = first.WebId;
        next.ListId = first.ListId;
        next.FolderUniqueId = first.FolderUniqueId;
        next.ProvenanceJson = "[{\"role\":\"ListDefaultDisplayForm\"}]";
        var writer = new SPFileWriter(fixture.CreateContext);
        await writer.WriteAsync(new[] { first });
        await writer.WriteAsync(new[] { next });
        var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext();
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var row = ReadRows(directory).Should().ContainSingle().Which;
            row.SiteCollectionId.Should().Be(first.SiteCollectionId);
            row.FileUniqueId.Should().Be(first.FileUniqueId);
            row.PageRole.Should().Be("ListForm");
            (await context.SPFiles.SingleAsync()).ProvenanceJson.Should().Contain("FolderFileObservation").And.Contain("ListDefaultDisplayForm");
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Preserves Unicode, quoted multiline errors, nullable metadata and the decoded physical locator while encoding percent/hash only in the exported URL.</summary>
    [Fact]
    public async Task TypedCsvPreservesFactsAndNormalizesOnlyExportUrl()
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        var fact = FileFact(scan);
        fact.ServerRelativeUrl = "/sites/test/測試%2f#?,\"renderer\".aspx";
        fact.FileName = "測試%2f#?,\"renderer\".aspx";
        fact.CustomizedPageStatus = string.Empty;
        fact.SetupPath = "features/template.aspx";
        fact.NoExecuteRaw = "1";
        fact.NoExecuteSource = "SPO.ListItem.NoExecute";
        fact.NoExecuteReadState = "Available";
        fact.FileErrorCodes = "HTTP403";
        fact.FileErrorDetail = "metadata,unavailable\nsecond-line";
        using var context = fixture.CreateContext();
        context.Add(fact);
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var row = ReadRows(directory).Single();
            row.Should().BeEquivalentTo(ClassicAspxReportProjection.Project(fact));
            row.Url.Should().Be("/sites/test/%E6%B8%AC%E8%A9%A6%252f%23%3F%2C%22renderer%22.aspx");
            row.NoExecuteRaw.Should().Be("1");
            row.IsPoisoned.Should().BeTrue();
            context.ChangeTracker.Clear();
            (await context.SPFiles.SingleAsync()).ServerRelativeUrl.Should().Be(fact.ServerRelativeUrl);
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Separates null, empty, literal sentinel, leading backslashes and multiline text in each reversible nullable string field.</summary>
    [Theory]
    [InlineData(null, @"\N")]
    [InlineData("", "")]
    [InlineData(@"\N", @"\\N")]
    [InlineData(@"\\N", @"\\\N")]
    [InlineData(@"\leading", @"\\leading")]
    [InlineData(@"inside\path", @"inside\path")]
    [InlineData("測試,\"quote\"\nline", "測試,\"quote\"\nline")]
    public async Task NullableTextEncodingIsReversibleAndDeclared(string? raw, string wire)
    {
        using var fixture = new ScanContextFixture();
        ClassicAspxNullableStringConverter.Encode(raw).Should().Be(wire);
        ClassicAspxNullableStringConverter.Decode(wire).Should().Be(raw);
        var scan = Guid.NewGuid();
        var fact = FileFact(scan);
        fact.SiteUrl = raw;
        fact.WebUrl = raw;
        fact.FileName = raw;
        fact.ContentTypeId = raw;
        fact.CustomizedPageStatus = raw;
        fact.SetupPath = raw;
        fact.NoExecuteRaw = raw;
        fact.FileErrorCodes = raw;
        fact.FileErrorDetail = raw;
        using var context = fixture.CreateContext();
        context.Add(fact);
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            ReadRows(directory).Single().Should().BeEquivalentTo(ClassicAspxReportProjection.Project(fact));
            using (var reader = new StreamReader(Path.Combine(directory, CsvName)))
            using (var csv = new CsvReader(reader, Configuration()))
            {
                csv.Read();
                csv.ReadHeader();
                csv.Read();
                foreach (var column in new[] { "SiteUrl", "WebUrl", "FileName", "ContentTypeId", "CustomizedPageStatus", "SetupPath", "NoExecuteRaw", "ErrorCodes", "ErrorDetail" })
                {
                    csv.GetField<string>(column).Should().Be(wire);
                }
            }
            (await context.SPFiles.AsNoTracking().SingleAsync()).SiteUrl.Should().Be(raw);
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, SummaryName)));
            summary.RootElement.GetProperty("nullableStringEncoding").GetString().Should().Be("BackslashNullV1");
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Retains ordinary nullable GUID and Boolean converters, distinguishing unknown poison from known false and true.</summary>
    [Fact]
    public async Task NullableGuidAndPoisonBooleanRoundTripNormally()
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        var unknown = FileFact(scan);
        unknown.WebId = null;
        unknown.ListId = null;
        var poisoned = FileFact(scan);
        poisoned.NoExecuteSource = "SPO.ListItem.NoExecute";
        poisoned.NoExecuteReadState = "Available";
        poisoned.NoExecuteRaw = "1";
        var clear = FileFact(scan);
        clear.NoExecuteSource = "SPO.ListItem.NoExecute";
        clear.NoExecuteReadState = "Available";
        clear.NoExecuteRaw = "0";
        using var context = fixture.CreateContext();
        context.AddRange(unknown, poisoned, clear);
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var rows = ReadRows(directory).ToDictionary(row => row.FileUniqueId);
            rows[unknown.FileUniqueId].WebId.Should().BeNull();
            rows[unknown.FileUniqueId].ListId.Should().BeNull();
            rows[unknown.FileUniqueId].IsPoisoned.Should().BeNull();
            rows[poisoned.FileUniqueId].IsPoisoned.Should().BeTrue();
            rows[clear.FileUniqueId].IsPoisoned.Should().BeFalse();
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Preserves the original NoExecute field string through SQLite and CSV, interpreting only available exact server zero/one values.</summary>
    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData(" 1 ", null)]
    [InlineData("01", null)]
    [InlineData("", null)]
    [InlineData("0x01000000", null)]
    public async Task OriginalNoExecuteTextIsNotNormalizedOrExpanded(string raw, bool? expected)
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        var fact = FileFact(scan);
        fact.NoExecuteRaw = raw;
        fact.NoExecuteSource = "SPO.ListItem.NoExecute";
        fact.NoExecuteReadState = "Available";
        using var context = fixture.CreateContext();
        context.Add(fact);
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var row = ReadRows(directory).Should().ContainSingle().Which;
            row.NoExecuteRaw.Should().Be(raw);
            row.IsPoisoned.Should().Be(expected);
            row.PoisonState.Should().Be(expected.HasValue ? expected.Value ? "Poisoned" : "NotPoisoned" : "Unknown");
            (await context.SPFiles.AsNoTracking().SingleAsync()).NoExecuteRaw.Should().Be(raw);
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, SummaryName)));
            summary.RootElement.GetProperty("poisonEvidenceKind").GetString().Should().Be("ServerNoExecuteField");
            summary.RootElement.GetProperty("fullDocFlagsCollected").GetBoolean().Should().BeFalse();
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Exports conflicting same-file NoExecute observations as unknown while retaining the writer's original raw value and both pieces of provenance.</summary>
    [Fact]
    public async Task ActualWriterNoExecuteConflictDoesNotBecomeFalse()
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        var first = FileFact(scan);
        first.NoExecuteRaw = "0";
        first.NoExecuteSource = "SPO.ListItem.NoExecute";
        first.NoExecuteReadState = "Available";
        var later = FileFact(scan, first.SiteCollectionId, first.FileUniqueId);
        later.WebId = first.WebId;
        later.ListId = first.ListId;
        later.FolderUniqueId = first.FolderUniqueId;
        later.NoExecuteRaw = "1";
        later.NoExecuteSource = "SPO.ListItem.NoExecute";
        later.NoExecuteReadState = "Available";
        var writer = new SPFileWriter(fixture.CreateContext);
        await writer.WriteAsync(new[] { first });
        await writer.WriteAsync(new[] { later });
        using var context = fixture.CreateContext();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            var row = ReadRows(directory).Should().ContainSingle().Which;
            row.NoExecuteRaw.Should().Be("1");
            row.IsPoisoned.Should().BeNull();
            row.PoisonState.Should().Be("Unknown");
            row.RenderDisposition.Should().Be("Unknown");
            (await context.SPFiles.AsNoTracking().SingleAsync()).ProvenanceJson.Should().Contain("NoExecuteRaw").And.Contain("metadata_changed");
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Emits the complete typed header for a legacy scan without inventing acquisition authority or historical physical rows.</summary>
    [Fact]
    public async Task EmptyExportHasTypedHeaderAndDoesNotClaimAcquisition()
    {
        using var fixture = new ScanContextFixture();
        var directory = DirectoryForTest();
        try
        {
            using var context = fixture.CreateContext();
            await ClassicAspxReportProjection.ExportCsvAsync(context, Guid.NewGuid(), directory, Configuration());
            Header(directory).Should().Equal(ExpectedHeader);
            ReadRows(directory).Should().BeEmpty();
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, SummaryName)));
            var root = summary.RootElement;
            root.GetProperty("projectionContract").ValueKind.Should().Be(JsonValueKind.Null);
            root.GetProperty("acquisitionState").GetString().Should().Be("NotAcquired/Legacy");
            root.GetProperty("scopeMode").GetString().Should().Be("Unknown");
            root.GetProperty("selectedClassicAspxCount").GetInt64().Should().Be(0);
            root.GetProperty("coverage").GetProperty("tenantCompleteness").GetString().Should().Be("NotAsserted");
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Does not mislabel observed raw facts as unacquired merely because every file is excluded by the business filter.</summary>
    [Fact]
    public async Task ModernOnlyObservationsDoNotBecomeLegacyAcquisition()
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        var modern = FileFact(scan);
        modern.ContentTypeId = ModernContentType;
        using var context = fixture.CreateContext();
        context.Add(modern);
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            ReadRows(directory).Should().BeEmpty();
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, SummaryName)));
            summary.RootElement.GetProperty("projectionContract").ValueKind.Should().Be(JsonValueKind.Null);
            summary.RootElement.GetProperty("acquisitionState").GetString().Should().Be("ObservedWithoutNativeMarker");
            summary.RootElement.GetProperty("selectedClassicAspxCount").GetInt64().Should().Be(0);
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Preserves scope declarations as read-only evidence and marks HOME as targeted without promoting any scope to tenant completeness.</summary>
    [Theory]
    [InlineData("DeclaredFullInventory", false)]
    [InlineData("HomePageOnly", true)]
    public async Task CurrentScopeExportsWithoutMutationOrCoveragePromotion(string scope, bool targeted)
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.Add(FileFact(scan));
        Mark(context, scan, scope);
        await context.SaveChangesAsync();
        var before = await context.Properties.AsNoTracking().Where(row => row.ScanId == scan)
            .OrderBy(row => row.Name).Select(row => new { row.Name, row.Value }).ToListAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, SummaryName)));
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

    /// <summary>Counts only selected business rows and explicitly identifies server NoExecute evidence without claiming complete DocFlags collection.</summary>
    [Fact]
    public async Task SummaryDescribesOnlyCompletedBusinessStream()
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        var unknown = FileFact(scan);
        var poisoned = FileFact(scan);
        poisoned.ContentTypeId = "0x010109";
        poisoned.CoverageState = "Incomplete";
        poisoned.NoExecuteRaw = "1";
        poisoned.NoExecuteReadState = "Available";
        poisoned.NoExecuteSource = "SPO.ListItem.NoExecute";
        var modern = FileFact(scan);
        modern.ContentTypeId = ModernContentType;
        modern.CoverageState = "ChangedDuringScan";
        var textFile = FileFact(scan);
        textFile.FileName = "other.txt";
        textFile.ServerRelativeUrl = "/sites/test/other.txt";
        using var context = fixture.CreateContext();
        context.AddRange(unknown, poisoned, modern, textFile);
        Mark(context, scan);
        context.Properties.Add(new Property { ScanId = scan, Name = "excludesitetemplates", Value = "none" });
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, SummaryName)));
            var root = summary.RootElement;
            root.GetProperty("schemaVersion").GetString().Should().Be("classic-aspx-summary/v1");
            root.GetProperty("scanId").GetGuid().Should().Be(scan);
            root.GetProperty("selectedClassicAspxCount").GetInt64().Should().Be(2);
            root.GetProperty("unknownClassificationCount").GetInt64().Should().Be(1);
            root.GetProperty("poisonedCount").GetInt64().Should().Be(1);
            root.GetProperty("poisonUnknownCount").GetInt64().Should().Be(1);
            root.GetProperty("noExecuteMissingCount").GetInt64().Should().Be(1);
            root.GetProperty("poisonEvidenceKind").GetString().Should().Be("ServerNoExecuteField");
            root.GetProperty("fullDocFlagsCollected").GetBoolean().Should().BeFalse();
            root.TryGetProperty("rawMaskCapability", out _).Should().BeFalse();
            root.GetProperty("templateExclusions").GetProperty("state").GetString().Should().Be("FrozenPolicy");
            root.GetProperty("templateExclusions").GetProperty("value").GetString().Should().Be("none");
            var coverage = root.GetProperty("coverage").GetProperty("selectedFileStates");
            coverage.GetProperty("Observed").GetInt64().Should().Be(1);
            coverage.GetProperty("Incomplete").GetInt64().Should().Be(1);
            coverage.TryGetProperty("ChangedDuringScan", out _).Should().BeFalse();
            var evidence = root.GetProperty("files").EnumerateArray().Should().ContainSingle().Which;
            evidence.GetProperty("name").GetString().Should().Be(CsvName);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, CsvName));
            evidence.GetProperty("length").GetInt64().Should().Be(bytes.Length);
            evidence.GetProperty("sha256").GetString().Should().Be(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Does not turn metadata references or legacy assessed pages into fake physical-file observations.</summary>
    [Fact]
    public async Task MetadataReferencesDoNotInventBusinessRows()
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        Mark(context, scan);
        context.ClassicPageDiscoveries.Add(new ClassicPageDiscovery
        {
            ScanId = scan, RecordKey = "metadata-only-reference", RowType = "Reference", ScopeType = "ListDefaultDisplayForm",
            DiscoveryStatus = "Complete", SiteUrl = "https://example.invalid/sites/test", WebUrl = "/",
            Url = "/sites/test/Forms/DispForm.aspx"
        });
        context.ClassicPages.Add(new ClassicPage { ScanId = scan, SiteUrl = "https://example.invalid/sites/test", WebUrl = "/", PageUrl = "/legacy.aspx" });
        await context.SaveChangesAsync();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            ReadRows(directory).Should().BeEmpty();
            (await context.SPFiles.CountAsync()).Should().Be(0);
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Exercises the real Classic report seam while retaining discovery and legacy CSVs alongside only the canonical ASPX business outputs.</summary>
    [Fact]
    public async Task NativeClassicDispatchPreservesDiscoveryAndLegacyCsvs()
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.Add(FileFact(scan));
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
            ReadRows(directory).Should().ContainSingle();
            Directory.GetFiles(directory).Select(Path.GetFileName).Should().Contain(CsvName).And.Contain(SummaryName)
                .And.NotContain("aspxinventory.csv").And.NotContain("aspxsummary.json").And.NotContain("spfiles.csv")
                .And.NotContain("classicaspxinventories.csv").And.NotContain("aspxitembindings.csv");
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Runs the previous-to-current migration path without creating historical SPFile facts from legacy assessment rows.</summary>
    [Fact]
    public async Task MigrationPreservesLegacyExportWithoutBackfill()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new ScanContext(new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options);
        var migrations = context.Database.GetMigrations().ToArray();
        await context.GetService<IMigrator>().MigrateAsync(migrations[^2]);
        var scan = Guid.NewGuid();
        context.ClassicPages.Add(new ClassicPage
        {
            ScanId = scan, SiteUrl = "https://example.invalid/sites/legacy", WebUrl = "/", PageUrl = "/old.aspx"
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await context.Database.MigrateAsync();
        (await context.SPFiles.CountAsync()).Should().Be(0);
        var directory = DirectoryForTest();
        try
        {
            await ReportManager.ExportClassicReportDataAsync(context, scan, directory, Configuration());
            using var reader = new StreamReader(Path.Combine(directory, "classicpages.csv"));
            using var csv = new CsvReader(reader, Configuration());
            csv.GetRecords<ClassicPage>().Single().PageUrl.Should().Be("/old.aspx");
            Header(directory).Should().Equal(ExpectedHeader);
            ReadRows(directory).Should().BeEmpty();
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Exercises a stream larger than one writer batch while leaving the exporting ScanContext untracked and retaining one row per native pair.</summary>
    [Fact]
    public async Task MultiBatchFactsExportWithoutTrackingOrAssessmentJoin()
    {
        using var fixture = new ScanContextFixture();
        var scan = Guid.NewGuid();
        var facts = Enumerable.Range(0, SPFileWriter.MaximumBatchSize * 2 + 1).Select(_ => FileFact(scan)).ToArray();
        var writer = new SPFileWriter(fixture.CreateContext);
        foreach (var batch in facts.Chunk(SPFileWriter.MaximumBatchSize))
        {
            await writer.WriteAsync(batch);
        }
        using var context = fixture.CreateContext();
        var directory = DirectoryForTest();
        try
        {
            await ClassicAspxReportProjection.ExportCsvAsync(context, scan, directory, Configuration());
            ReadRows(directory).Select(row => (row.SiteCollectionId, row.FileUniqueId)).Should().HaveCount(facts.Length).And.OnlyHaveUniqueItems();
            context.ChangeTracker.Entries().Should().BeEmpty();
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }

    /// <summary>Retains the physical file and a nullable export URL when the stored locator cannot pass the existing site-boundary policy.</summary>
    [Theory]
    [InlineData("/sites/other/page.aspx")]
    [InlineData("/sites/test/../page.aspx")]
    [InlineData("https://foreign.invalid/sites/test/page.aspx")]
    public void UnsafeLocatorNeverRemovesObservedGuidIdentity(string locator)
    {
        var fact = FileFact(Guid.NewGuid());
        fact.ServerRelativeUrl = locator;
        var row = ClassicAspxReportProjection.Project(fact);
        row.Should().NotBeNull();
        row!.Url.Should().BeNull();
        row.FileUniqueId.Should().Be(fact.FileUniqueId);
        fact.ServerRelativeUrl.Should().Be(locator);
    }

    /// <summary>Propagates caller cancellation instead of producing an apparently complete summary for an interrupted stream.</summary>
    [Fact]
    public async Task CancelledStreamDoesNotPublishSummary()
    {
        using var fixture = new ScanContextFixture();
        using var context = fixture.CreateContext();
        var directory = DirectoryForTest();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            Func<Task> export = () => ClassicAspxReportProjection.ExportCsvAsync(context, Guid.NewGuid(), directory, Configuration(), cancellation.Token);
            await export.Should().ThrowAsync<OperationCanceledException>();
            File.Exists(Path.Combine(directory, SummaryName)).Should().BeFalse();
        }
        finally
        {
            DeleteOwnDirectory(directory);
        }
    }
}
