#nullable enable
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

public sealed class ClassicAspxInventorySchemaTests
{
    private static readonly string[] ExpectedFields =
    {
        "ScanId", "AspxKey", "SiteCollectionId", "WebId", "ListId", "FolderUniqueId", "FileUniqueId",
        "SiteUrl", "WebUrl", "Url", "FileName", "ContentTypeId", "CustomizedPageStatus", "SetupPath",
        "FileReadState", "FileErrorCodes", "FileErrorDetail", "ProvenanceJson", "CoverageState",
        "FirstObservedAtUtc", "ObservedAtUtc"
    };

    /// <summary>Creates a context over the caller-owned connection so separate units of work inspect the same migrated database.</summary>
    private static ScanContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options);

    /// <summary>Creates physical-file inventory with independent observation slots and no synthetic identity inputs.</summary>
    private static ClassicAspxInventory Tenant(Guid scan, Guid site, Guid file) => new()
    {
        ScanId = scan,
        AspxKey = ClassicAspxInventory.CreateTenantFileKey(site, file),
        SiteCollectionId = site,
        FileUniqueId = file,
        WebId = Guid.NewGuid(),
        ListId = Guid.NewGuid(),
        FolderUniqueId = Guid.NewGuid(),
        SiteUrl = "https://example.invalid/sites/test",
        WebUrl = "/",
        Url = "/sites/test/renderer.aspx",
        FileName = "renderer.aspx",
        FileReadState = "Available",
        ProvenanceJson = "[]",
        CoverageState = "Observed",
        FirstObservedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ObservedAtUtc = new DateTime(2026, 1, 1, 0, 2, 0, DateTimeKind.Utc)
    };

    /// <summary>Requires tenant:v1 identity to depend only on nonempty site-collection and file GUIDs, never web/list/folder/path metadata.</summary>
    [Fact]
    public void TenantKeyUsesOnlyRealSiteAndFileGuids()
    {
        var scan = Guid.NewGuid();
        var site = Guid.NewGuid();
        var file = Guid.NewGuid();
        var first = Tenant(scan, site, file);
        var second = Tenant(scan, site, file);
        second.Url = "/renamed.aspx";
        first.AspxKey.Should().Be($"tenant:v1:{site:N}:{file:N}").And.Be(second.AspxKey);
        ClassicAspxInventory.CreateTenantFileKey(Guid.NewGuid(), file).Should().NotBe(first.AspxKey);
        ClassicAspxInventory.CreateTenantFileKey(site, Guid.NewGuid()).Should().NotBe(first.AspxKey);
        FluentActions.Invoking(() => ClassicAspxInventory.CreateTenantFileKey(Guid.Empty, file)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ClassicAspxInventory.CreateTenantFileKey(site, Guid.Empty)).Should().Throw<ArgumentException>();
    }

    /// <summary>Locks entity and CSV to the same 21 field names and types, including required physical IDs and nullable observation GUIDs.</summary>
    [Fact]
    public void EntityAndExportHaveExactlyTheApprovedFieldsAndTypes()
    {
        var entity = typeof(ClassicAspxInventory).GetProperties();
        var exported = typeof(ClassicAspxInventoryExport).GetProperties();
        entity.Select(property => property.Name).Should().Equal(ExpectedFields);
        exported.Select(property => property.Name).Should().Equal(ExpectedFields);
        exported.Select(property => property.PropertyType).Should().Equal(entity.Select(property => property.PropertyType));
        entity.Single(property => property.Name == "SiteCollectionId").PropertyType.Should().Be(typeof(Guid));
        entity.Single(property => property.Name == "FileUniqueId").PropertyType.Should().Be(typeof(Guid));
        foreach (var name in new[] { "WebId", "ListId", "FolderUniqueId" })
        {
            entity.Single(property => property.Name == name).PropertyType.Should().Be(typeof(Guid?));
        }
    }

    /// <summary>Maps only inventory, its scan/key primary key, and its unfiltered real-file unique index without obsolete relationship tables.</summary>
    [Fact]
    public void SchemaUsesPhysicalFileKeysAndNoBindingRelationships()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = Context(connection);
        var inventory = context.Model.FindEntityType(typeof(ClassicAspxInventory))!;
        inventory.GetTableName().Should().Be("ClassicAspxInventories");
        inventory.FindPrimaryKey()!.Properties.Select(property => property.Name).Should().Equal("ScanId", "AspxKey");
        inventory.GetProperties().Select(property => property.Name).Should().BeEquivalentTo(ExpectedFields);
        inventory.FindProperty("SiteCollectionId")!.IsNullable.Should().BeFalse();
        inventory.FindProperty("FileUniqueId")!.IsNullable.Should().BeFalse();
        foreach (var name in new[] { "WebId", "ListId", "FolderUniqueId" })
        {
            inventory.FindProperty(name)!.IsNullable.Should().BeTrue();
        }
        inventory.GetForeignKeys().Should().BeEmpty();
        var index = inventory.GetIndexes().Should().ContainSingle().Which;
        index.IsUnique.Should().BeTrue();
        index.Properties.Select(property => property.Name).Should().Equal("ScanId", "SiteCollectionId", "FileUniqueId");
        index.GetFilter().Should().BeNull();
        context.Model.GetEntityTypes().Select(entity => entity.GetTableName()).Should().NotContain("ClassicAspxItemBindings");
    }

    /// <summary>Round-trips all inventory observation facts, including nullable text, read failures, provenance, coverage, and time bounds.</summary>
    [Fact]
    public async Task InventoryObservationFactsRoundTripWithoutSourceOrItemModels()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        inventory.ContentTypeId = "0x0101";
        inventory.CustomizedPageStatus = "Customized";
        inventory.SetupPath = null;
        inventory.FileReadState = "Unavailable";
        inventory.FileErrorCodes = "HTTP403";
        inventory.FileErrorDetail = "denied, quoted\nsecond line";
        inventory.ProvenanceJson = "[{\"source\":\"FolderFileObservation\"}]";
        inventory.CoverageState = "Incomplete";
        context.Add(inventory);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var saved = await context.ClassicAspxInventories.SingleAsync();
        saved.Should().BeEquivalentTo(inventory);
    }

    /// <summary>Persists a root ASPX file without optional web/list/folder ownership and without fabricating a related item.</summary>
    [Fact]
    public async Task RootFileAllowsAbsentObservationSlots()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        inventory.WebId = null;
        inventory.ListId = null;
        inventory.FolderUniqueId = null;
        context.Add(inventory);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var saved = await context.ClassicAspxInventories.SingleAsync();
        saved.WebId.Should().BeNull();
        saved.ListId.Should().BeNull();
        saved.FolderUniqueId.Should().BeNull();
        saved.AspxKey.Should().Be(inventory.AspxKey);
    }

    /// <summary>Rejects duplicate scan/key rows at the database primary-key boundary across independent contexts.</summary>
    [Fact]
    public async Task PrimaryKeyRejectsDuplicateInventory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var context = Context(connection))
        {
            await context.Database.MigrateAsync();
            context.Add(inventory);
            await context.SaveChangesAsync();
        }
        await using var duplicate = Context(connection);
        duplicate.Add(Tenant(inventory.ScanId, inventory.SiteCollectionId, inventory.FileUniqueId));
        await FluentActions.Awaiting(() => duplicate.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>Prevents a second identity for the same scan/site/file even when its free-text key and location are changed.</summary>
    [Fact]
    public async Task PhysicalFileUniquenessCannotBeBypassedWithAnotherKeyOrPath()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        context.Add(inventory);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var duplicate = Tenant(inventory.ScanId, inventory.SiteCollectionId, inventory.FileUniqueId);
        duplicate.AspxKey = "another-free-text-key";
        duplicate.Url = "/renamed.aspx";
        context.Add(duplicate);
        await FluentActions.Awaiting(() => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>Keeps file GUIDs scoped by real site collection and keeps the same physical file independently observed across assessments.</summary>
    [Fact]
    public async Task SiteCollectionAndAssessmentScopesRemainDistinct()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        context.AddRange(inventory,
            Tenant(inventory.ScanId, Guid.NewGuid(), inventory.FileUniqueId),
            Tenant(Guid.NewGuid(), inventory.SiteCollectionId, inventory.FileUniqueId));
        await context.SaveChangesAsync();
        (await context.ClassicAspxInventories.CountAsync()).Should().Be(3);
    }

    /// <summary>Rejects empty physical IDs and empty keys even when callers bypass the writer and insert through EF directly.</summary>
    [Theory]
    [InlineData("SiteCollectionId")]
    [InlineData("FileUniqueId")]
    [InlineData("AspxKey")]
    public async Task DatabaseRequiresRealPhysicalIdentity(string field)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        if (field == "SiteCollectionId")
        {
            inventory.SiteCollectionId = Guid.Empty;
        }
        if (field == "FileUniqueId")
        {
            inventory.FileUniqueId = Guid.Empty;
        }
        if (field == "AspxKey")
        {
            inventory.AspxKey = string.Empty;
        }
        context.Add(inventory);
        await FluentActions.Awaiting(() => context.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>Applies exactly the 21-column generated inventory table and verifies the removed binding table is absent from SQLite.</summary>
    [Fact]
    public async Task MigratedDatabaseHasOnlyApprovedInventoryColumns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        await context.Database.MigrateAsync();
        await using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info('ClassicAspxInventories')";
        var names = new List<string>();
        await using (var reader = await columns.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(1));
            }
        }
        names.Should().BeEquivalentTo(ExpectedFields);
        await using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ClassicAspxItemBindings'";
        Convert.ToInt64(await tables.ExecuteScalarAsync()).Should().Be(0);
    }

    /// <summary>Upgrades, downgrades, and reapplies the tool-generated migration without losing legacy pages or backfilling physical observations.</summary>
    [Fact]
    public async Task GeneratedMigrationPreservesLegacyRowsWithoutBackfill()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = Context(connection);
        var migrations = context.Database.GetMigrations().ToArray();
        migrations.Last().Should().EndWith("_ClassicAspxInventoryOnly");
        var prior = migrations[^2];
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
        await migrator.MigrateAsync(migrations.Last());
        (await context.ClassicPages.SingleAsync()).ScanId.Should().Be(scan);
        (await context.ClassicAspxInventories.CountAsync()).Should().Be(0);
        context.Add(Tenant(scan, Guid.NewGuid(), Guid.NewGuid()));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await migrator.MigrateAsync(prior);
        context.ChangeTracker.Clear();
        (await context.ClassicPages.SingleAsync()).CustomizedPageStatus.Should().Be("Uncustomized");
        await migrator.MigrateAsync(migrations.Last());
        context.ChangeTracker.Clear();
        (await context.ClassicPages.CountAsync()).Should().Be(1);
        (await context.ClassicAspxInventories.CountAsync()).Should().Be(0);
    }

    /// <summary>Requires the generated model snapshot to match the runtime model after Lead regenerates the inventory-only migration.</summary>
    [Fact]
    public void GeneratedSnapshotHasNoPendingModelChanges()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = Context(connection);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
