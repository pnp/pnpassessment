#nullable enable
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

public sealed class SPFileSchemaTests
{
    private static readonly IReadOnlyDictionary<string, Type> ExpectedFields = new Dictionary<string, Type>
    {
        ["SiteCollectionId"] = typeof(Guid),
        ["FileUniqueId"] = typeof(Guid),
        ["ScanId"] = typeof(Guid),
        ["WebId"] = typeof(Guid?),
        ["ListId"] = typeof(Guid?),
        ["FolderUniqueId"] = typeof(Guid?),
        ["ListItemId"] = typeof(int?),
        ["Length"] = typeof(long?),
        ["SiteUrl"] = typeof(string),
        ["WebUrl"] = typeof(string),
        ["ServerRelativeUrl"] = typeof(string),
        ["FileName"] = typeof(string),
        ["ContentTypeId"] = typeof(string),
        ["CustomizedPageStatus"] = typeof(string),
        ["SetupPath"] = typeof(string),
        ["FileReadState"] = typeof(string),
        ["FileErrorCodes"] = typeof(string),
        ["FileErrorDetail"] = typeof(string),
        ["ProvenanceJson"] = typeof(string),
        ["CoverageState"] = typeof(string),
        ["FirstObservedAtUtc"] = typeof(DateTime),
        ["ObservedAtUtc"] = typeof(DateTime),
        ["ClientSideApplicationId"] = typeof(string),
        ["HtmlFileType"] = typeof(string),
        ["FileType"] = typeof(string),
        ["BSN"] = typeof(string),
        ["WikiFieldHasValue"] = typeof(bool?),
        ["ItemMetadataReadState"] = typeof(string),
        ["ItemMetadataFieldStatesJson"] = typeof(string),
        ["NoExecuteRaw"] = typeof(string),
        ["NoExecuteSource"] = typeof(string),
        ["NoExecuteReadState"] = typeof(string)
    };

    /// <summary>Creates a context over the caller-owned connection so independent units of work inspect the same migrated schema.</summary>
    private static ScanContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options);

    /// <summary>Creates physical-file facts with no synthetic identity, classification columns, or required item relationship.</summary>
    private static SPFile File(Guid scan, Guid site, Guid file) => new()
    {
        SiteCollectionId = site,
        FileUniqueId = file,
        ScanId = scan,
        WebId = Guid.NewGuid(),
        ListId = Guid.NewGuid(),
        FolderUniqueId = Guid.NewGuid(),
        SiteUrl = "https://example.invalid/sites/test",
        WebUrl = "/",
        ServerRelativeUrl = "/sites/test/renderer.aspx",
        FileName = "renderer.aspx",
        FileReadState = "Available",
        ProvenanceJson = "[]",
        CoverageState = "Observed",
        FirstObservedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ObservedAtUtc = new DateTime(2026, 1, 1, 0, 2, 0, DateTimeKind.Utc)
    };

    /// <summary>Locks the raw fact entity to the approved 32 properties and CLR types without coupling the entity to business CSV fields.</summary>
    [Fact]
    public void EntityHasExactlyTheApprovedRawFieldsAndTypes()
    {
        var properties = typeof(SPFile).GetProperties().ToDictionary(property => property.Name, property => property.PropertyType);
        properties.Keys.Should().BeEquivalentTo(ExpectedFields.Keys);
        foreach (var expected in ExpectedFields)
        {
            properties[expected.Key].Should().Be(expected.Value);
        }
        properties.Should().HaveCount(32);
        properties.Keys.Should().NotContain(new[] { "AspxKey", "Url", "IsClassic", "IsPoisoned", "PageType", "DocFlagsRaw", "DocFlagsSource", "DocFlagsReadState" });
    }

    /// <summary>Maps exactly one explicit SPFile table with the real pair as its only key and no redundant unique index or item foreign key.</summary>
    [Fact]
    public void SchemaUsesOnlySiteCollectionAndFileGuidAsPrimaryKey()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = Context(connection);
        var file = context.Model.FindEntityType(typeof(SPFile))!;
        file.GetTableName().Should().Be("SPFile");
        file.FindPrimaryKey()!.Properties.Select(property => property.Name).Should().Equal("SiteCollectionId", "FileUniqueId");
        file.GetProperties().Select(property => property.Name).Should().BeEquivalentTo(ExpectedFields.Keys);
        file.GetIndexes().Should().BeEmpty();
        file.GetForeignKeys().Should().BeEmpty();
        foreach (var name in new[] { "SiteCollectionId", "FileUniqueId", "ScanId", "FileReadState", "ProvenanceJson", "CoverageState", "ItemMetadataReadState", "ItemMetadataFieldStatesJson", "NoExecuteReadState" })
        {
            file.FindProperty(name)!.IsNullable.Should().BeFalse();
        }
        foreach (var name in new[] { "WebId", "ListId", "FolderUniqueId", "ListItemId", "Length", "WikiFieldHasValue", "NoExecuteRaw", "NoExecuteSource", "ClientSideApplicationId", "BSN" })
        {
            file.FindProperty(name)!.IsNullable.Should().BeTrue();
        }
        context.Model.GetEntityTypes().Select(entity => entity.GetTableName())
            .Should().NotContain(new[] { "ClassicAspxInventories", "ClassicAspxItemBindings" });
    }

    /// <summary>Round-trips all 32 raw facts, including long sizes, empty values, nullable booleans, original paths, and uninterpreted NoExecute field text.</summary>
    [Fact]
    public async Task AllRawObservationFactsRoundTripWithoutDerivedBusinessState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        var file = File(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        file.ListItemId = 42;
        file.Length = 1234567890123;
        file.ServerRelativeUrl = "/sites/test/100%25?# Page.aspx";
        file.ContentTypeId = "0x0101";
        file.CustomizedPageStatus = "Customized";
        file.SetupPath = "features/raw.aspx";
        file.FileReadState = "Unavailable";
        file.FileErrorCodes = "HTTP403";
        file.FileErrorDetail = "denied, quoted\nsecond line";
        file.ProvenanceJson = "[{\"source\":\"FolderFileObservation\"}]";
        file.CoverageState = "Incomplete";
        file.ClientSideApplicationId = "00000000-0000-0000-0000-000000000000";
        file.HtmlFileType = "";
        file.FileType = "aspx";
        file.BSN = "raw-bsn";
        file.WikiFieldHasValue = false;
        file.ItemMetadataReadState = "Available";
        file.ItemMetadataFieldStatesJson = "{\"WikiField\":\"value\",\"field-returned-null\":\"null\",\"field-omitted\":\"omitted\"}";
        file.NoExecuteRaw = " 1 ";
        file.NoExecuteSource = "synthetic-noexecute-source";
        file.NoExecuteReadState = "Available";
        context.Add(file);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        (await context.SPFiles.SingleAsync()).Should().BeEquivalentTo(file);
    }

    /// <summary>Persists root or otherwise itemless files while leaving optional raw facts absent rather than manufacturing IDs or a zero NoExecute field value.</summary>
    [Fact]
    public async Task FileAllowsAbsentObservationSlots()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        var file = File(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        file.WebId = null;
        file.ListId = null;
        file.FolderUniqueId = null;
        context.Add(file);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var saved = await context.SPFiles.SingleAsync();
        saved.WebId.Should().BeNull();
        saved.ListId.Should().BeNull();
        saved.FolderUniqueId.Should().BeNull();
        saved.ListItemId.Should().BeNull();
        saved.Length.Should().BeNull();
        saved.WikiFieldHasValue.Should().BeNull();
        saved.NoExecuteRaw.Should().BeNull();
        saved.NoExecuteSource.Should().BeNull();
        saved.ItemMetadataFieldStatesJson.Should().Be("{}");
        saved.ItemMetadataReadState.Should().Be("NotAttempted");
        saved.NoExecuteReadState.Should().Be("NotAttempted");
    }

    /// <summary>Rejects a duplicate physical pair across both same-scan and cross-scan direct EF inserts, regardless of changed path metadata.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryKeyCannotBeBypassedByChangingScanOrPath(bool otherScan)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var original = File(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var context = Context(connection))
        {
            await context.Database.MigrateAsync();
            context.Add(original);
            await context.SaveChangesAsync();
        }
        await using var duplicate = Context(connection);
        var changed = File(otherScan ? Guid.NewGuid() : original.ScanId, original.SiteCollectionId, original.FileUniqueId);
        changed.ServerRelativeUrl = "/renamed.aspx";
        duplicate.Add(changed);
        await FluentActions.Awaiting(() => duplicate.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        await using var retained = Context(connection);
        (await retained.SPFiles.SingleAsync()).ScanId.Should().Be(original.ScanId);
    }

    /// <summary>Preserves distinct site collections even when they return the same file GUID under one scan.</summary>
    [Fact]
    public async Task SiteCollectionScopesRemainDistinct()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        var file = File(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        context.AddRange(file, File(file.ScanId, Guid.NewGuid(), file.FileUniqueId));
        await context.SaveChangesAsync();
        (await context.SPFiles.CountAsync()).Should().Be(2);
    }

    /// <summary>Rejects empty physical GUIDs at the SQLite boundary even when an insertion bypasses writer validation.</summary>
    [Theory]
    [InlineData("SiteCollectionId")]
    [InlineData("FileUniqueId")]
    public async Task DatabaseRequiresRealPhysicalIdentity(string field)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        var file = File(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        if (field == "SiteCollectionId")
        {
            file.SiteCollectionId = Guid.Empty;
        }
        else
        {
            file.FileUniqueId = Guid.Empty;
        }
        context.Add(file);
        await FluentActions.Awaiting(() => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>Requires the final generated migration to create exactly the 32 approved SPFile columns without legacy synthetic-identity tables.</summary>
    [Fact]
    public async Task MigratedDatabaseHasOnlyApprovedRawColumns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        await using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info('SPFile')";
        var names = new List<string>();
        await using (var reader = await columns.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(1));
            }
        }
        names.Should().HaveCount(32).And.BeEquivalentTo(ExpectedFields.Keys);
        await using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('ClassicAspxInventories', 'ClassicAspxItemBindings')";
        Convert.ToInt64(await tables.ExecuteScalarAsync()).Should().Be(0);
    }

    /// <summary>Upgrades, downgrades, and reapplies the final tool-generated migration without deleting legacy pages or inventing physical observations.</summary>
    [Fact]
    public async Task GeneratedMigrationPreservesLegacyRowsWithoutBackfill()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        var migrations = context.Database.GetMigrations().ToArray();
        migrations.Length.Should().BeGreaterThan(1);
        var prior = migrations[^2];
        var latest = migrations[^1];
        latest.Should().EndWith("_SPFileFacts");
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(prior);
        var scan = Guid.NewGuid();
        context.ClassicPages.Add(new ClassicPage
        {
            ScanId = scan,
            SiteUrl = "https://example.invalid/sites/legacy",
            WebUrl = "/",
            PageUrl = "/old.aspx",
            CustomizedPageStatus = "Uncustomized"
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await migrator.MigrateAsync(latest);
        (await context.ClassicPages.SingleAsync()).ScanId.Should().Be(scan);
        (await context.SPFiles.CountAsync()).Should().Be(0);
        context.Add(File(scan, Guid.NewGuid(), Guid.NewGuid()));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await migrator.MigrateAsync(prior);
        context.ChangeTracker.Clear();
        (await context.ClassicPages.SingleAsync()).CustomizedPageStatus.Should().Be("Uncustomized");
        await migrator.MigrateAsync(latest);
        context.ChangeTracker.Clear();
        (await context.ClassicPages.CountAsync()).Should().Be(1);
        (await context.SPFiles.CountAsync()).Should().Be(0);
    }

    /// <summary>Requires the generated snapshot to match the runtime raw-file model after Lead completes EF tooling.</summary>
    [Fact]
    public void GeneratedSnapshotHasNoPendingModelChanges()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = Context(connection);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
