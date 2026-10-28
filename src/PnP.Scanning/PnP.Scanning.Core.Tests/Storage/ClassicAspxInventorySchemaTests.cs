#nullable enable
using FluentAssertions;
using Xunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Tests.Storage;

public sealed class ClassicAspxInventorySchemaTests
{
    private static ScanContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options);

    private static ClassicAspxInventory Tenant(Guid scan, Guid site, Guid file) => new()
    {
        ScanId = scan, AspxKey = ClassicAspxInventory.CreateTenantFileKey(site, file),
        IdentityKind = ClassicAspxIdentityKinds.TenantFile, IdentityVersion = 1,
        SiteCollectionId = site, FileUniqueId = file, WebId = Guid.NewGuid(),
        SiteUrl = "https://example.invalid/sites/test", WebUrl = "/", Url = "/sites/test/renderer.aspx",
        FileReadState = "Read", HandlerReadState = "Read", HandlerParseState = "Parsed",
        ProvenanceJson = "[]", FirstObservedAtUtc = DateTime.UtcNow, ObservedAtUtc = DateTime.UtcNow
    };

    private static ClassicAspxInventory Platform(Guid scan) => new()
    {
        ScanId = scan, IdentityKind = ClassicAspxIdentityKinds.PlatformCatalog, IdentityVersion = 1,
        CatalogSource = "public-frozen-catalog", CatalogVersion = "test-v1", CatalogDigest = new string('a', 64),
        CatalogEntryKey = "layouts/15/listform.aspx",
        AspxKey = ClassicAspxInventory.CreatePlatformKey("public-frozen-catalog", "test-v1", new string('a', 64), "layouts/15/listform.aspx"),
        FileReadState = "CatalogOnly", HandlerReadState = "NotAttempted", HandlerParseState = "NotAttempted",
        ProvenanceJson = "[]", FirstObservedAtUtc = DateTime.UtcNow, ObservedAtUtc = DateTime.UtcNow
    };

    private static ClassicAspxItemBinding Binding(ClassicAspxInventory inventory, Guid web, Guid list, int item) => new()
    {
        ScanId = inventory.ScanId, AspxKey = inventory.AspxKey,
        SiteCollectionId = inventory.SiteCollectionId ?? Guid.NewGuid(), WebId = web, ListId = list, ItemId = item,
        ItemGuid = null, ItemGuidReadState = "Unavailable", BindingKinds = ClassicAspxBindingKinds.ListFormRenderer,
        BindingProvenanceJson = "[]", ObservedAtUtc = DateTime.UtcNow
    };

    [Fact]
    public void TenantKeyUsesRealSiteAndFileGuidsNotWebOrPath()
    {
        var scan = Guid.NewGuid(); var site = Guid.NewGuid(); var file = Guid.NewGuid();
        var first = Tenant(scan, site, file); var second = Tenant(scan, site, file);
        second.WebId = Guid.NewGuid(); second.Url = "/renamed.aspx";
        first.AspxKey.Should().Be(second.AspxKey);
        ClassicAspxInventory.CreateTenantFileKey(Guid.NewGuid(), file).Should().NotBe(first.AspxKey);
        ClassicAspxInventory.CreateTenantFileKey(site, Guid.NewGuid()).Should().NotBe(first.AspxKey);
        Action missingSite = () => ClassicAspxInventory.CreateTenantFileKey(Guid.Empty, file);
        Action missingFile = () => ClassicAspxInventory.CreateTenantFileKey(site, Guid.Empty);
        missingSite.Should().Throw<ArgumentException>(); missingFile.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PlatformKeyUsesFrozenDeploymentAndEntryNeverWeb()
    {
        var first = Platform(Guid.NewGuid()); var second = Platform(first.ScanId);
        second.WebId = Guid.NewGuid(); second.WebUrl = "/other-web";
        first.AspxKey.Should().Be(second.AspxKey);
        ClassicAspxInventory.CreatePlatformKey(first.CatalogSource!, "test-v2", first.CatalogDigest!, first.CatalogEntryKey!).Should().NotBe(first.AspxKey);
        ClassicAspxInventory.CreatePlatformKey(first.CatalogSource!, first.CatalogVersion!, new string('b', 64), first.CatalogEntryKey!).Should().NotBe(first.AspxKey);
        Action missing = () => ClassicAspxInventory.CreatePlatformKey("", "v1", new string('a', 64), "x.aspx");
        missing.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task RootAspxWithoutBackingItemHasOneInventoryAndNoInventedBinding()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        await using var context = Context(connection); await context.Database.MigrateAsync();
        context.Add(Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())); await context.SaveChangesAsync(); context.ChangeTracker.Clear();
        (await context.ClassicAspxInventories.CountAsync()).Should().Be(1);
        (await context.ClassicAspxItemBindings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OneRendererHasOneHundredRealIntegerBindingsWithoutSupplementaryItemGuids()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        await using var context = Context(connection); await context.Database.MigrateAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); var web = Guid.NewGuid(); var list = Guid.NewGuid();
        context.Add(inventory); context.ClassicAspxItemBindings.AddRange(Enumerable.Range(1, 100).Select(i => Binding(inventory, web, list, i)));
        await context.SaveChangesAsync(); context.ChangeTracker.Clear();
        (await context.ClassicAspxInventories.CountAsync()).Should().Be(1);
        var bindings = await context.ClassicAspxItemBindings.ToListAsync();
        bindings.Should().HaveCount(100); bindings.Select(b => b.ItemId).Distinct().Should().HaveCount(100);
        bindings.Should().OnlyContain(b => b.ItemGuid == null && b.ItemGuidReadState == "Unavailable");
    }

    [Fact]
    public async Task SourceFlagsAndProvenanceMergeWithoutMultiplyingTheKey()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        await using var context = Context(connection); await context.Database.MigrateAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); var binding = Binding(inventory, Guid.NewGuid(), Guid.NewGuid(), 1);
        binding.BindingKinds |= ClassicAspxBindingKinds.ContentTypeRenderer;
        binding.BindingProvenanceJson = "[{\"source\":\"ListForm\"},{\"source\":\"ContentType\"}]";
        context.Add(inventory); context.Add(binding); await context.SaveChangesAsync(); context.ChangeTracker.Clear();
        var saved = await context.ClassicAspxItemBindings.SingleAsync();
        saved.BindingKinds.Should().Be(ClassicAspxBindingKinds.ListFormRenderer | ClassicAspxBindingKinds.ContentTypeRenderer);
        saved.BindingProvenanceJson.Should().Be(binding.BindingProvenanceJson);
    }

    [Fact]
    public async Task NullableHandlerSourceDefaultAndCoverageEvidenceRoundTrip()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        await using var context = Context(connection); await context.Database.MigrateAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        inventory.FirstObservedAtUtc = first; inventory.ObservedAtUtc = first.AddMinutes(2);
        inventory.FileReadState = "Denied"; inventory.HandlerReadState = "Unavailable"; inventory.HandlerParseState = "NotAttempted";
        inventory.HandlerDeclaredType = null; inventory.HandlerNormalizedType = null; inventory.HandlerEffectiveType = null;
        inventory.HandlerSource = "FileBody"; inventory.HandlerDefaultProvenance = null; inventory.HandlerReasonCode = "body_unavailable";
        inventory.HandlerErrorCodes = "HTTP403"; inventory.CoverageState = "Incomplete";
        context.Add(inventory); await context.SaveChangesAsync(); context.ChangeTracker.Clear();
        var saved = await context.ClassicAspxInventories.SingleAsync();
        saved.HandlerDeclaredType.Should().BeNull(); saved.HandlerNormalizedType.Should().BeNull(); saved.HandlerEffectiveType.Should().BeNull();
        saved.HandlerSource.Should().Be("FileBody"); saved.HandlerDefaultProvenance.Should().BeNull(); saved.HandlerReasonCode.Should().Be("body_unavailable");
        saved.FileReadState.Should().Be("Denied"); saved.HandlerReadState.Should().Be("Unavailable"); saved.HandlerParseState.Should().Be("NotAttempted");
        saved.CoverageState.Should().Be("Incomplete"); saved.FirstObservedAtUtc.Should().Be(first); saved.ObservedAtUtc.Should().Be(first.AddMinutes(2));
        saved.HandlerDefaultProvenance = "known-family-default"; saved.HandlerEffectiveType = "Synthetic.DefaultHandler";
        await context.SaveChangesAsync(); context.ChangeTracker.Clear();
        (await context.ClassicAspxInventories.SingleAsync()).HandlerDefaultProvenance.Should().Be("known-family-default");
    }

    [Fact]
    public async Task PrimaryKeysRejectDuplicateInventoryAndNormalizedBinding()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); var binding = Binding(inventory, Guid.NewGuid(), Guid.NewGuid(), 1);
        await using (var context = Context(connection)) { await context.Database.MigrateAsync(); context.Add(inventory); context.Add(binding); await context.SaveChangesAsync(); }
        await using (var duplicateInventory = Context(connection))
        {
            duplicateInventory.Add(Tenant(inventory.ScanId, inventory.SiteCollectionId!.Value, inventory.FileUniqueId!.Value));
            Func<Task> insert = () => duplicateInventory.SaveChangesAsync(); await insert.Should().ThrowAsync<DbUpdateException>();
        }
        await using (var duplicateBinding = Context(connection))
        {
            duplicateBinding.Add(Binding(inventory, binding.WebId, binding.ListId, binding.ItemId));
            Func<Task> insert = () => duplicateBinding.SaveChangesAsync(); await insert.Should().ThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task PhysicalFileUniquenessIsNotAUrlOrFreeTextKeyMultiplicity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var context = Context(connection)) { await context.Database.MigrateAsync(); context.Add(inventory); await context.SaveChangesAsync(); }
        await using var duplicate = Context(connection);
        var same = Tenant(inventory.ScanId, inventory.SiteCollectionId!.Value, inventory.FileUniqueId!.Value);
        same.AspxKey = "not-a-second-resource"; same.Url = "/renamed.aspx"; duplicate.Add(same);
        Func<Task> insert = () => duplicate.SaveChangesAsync(); await insert.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task CompositeForeignKeyRejectsCrossAssessmentResource()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using (var context = Context(connection)) { await context.Database.MigrateAsync(); context.Add(inventory); await context.SaveChangesAsync(); }
        await using var wrong = Context(connection); var binding = Binding(inventory, Guid.NewGuid(), Guid.NewGuid(), 1); binding.ScanId = Guid.NewGuid(); wrong.Add(binding);
        Func<Task> insert = () => wrong.SaveChangesAsync(); await insert.Should().ThrowAsync<DbUpdateException>();
    }

    [Theory]
    [InlineData("SiteCollectionId")]
    [InlineData("WebId")]
    [InlineData("ListId")]
    [InlineData("ItemId")]
    public async Task ItemKeyRequiresRealScopeGuidsAndPositiveIntegerId(string field)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        await using var context = Context(connection); await context.Database.MigrateAsync();
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); var binding = Binding(inventory, Guid.NewGuid(), Guid.NewGuid(), 1);
        if (field == "SiteCollectionId") binding.SiteCollectionId = Guid.Empty;
        if (field == "WebId") binding.WebId = Guid.Empty;
        if (field == "ListId") binding.ListId = Guid.Empty;
        if (field == "ItemId") binding.ItemId = 0;
        context.Add(inventory); context.Add(binding); Func<Task> insert = () => context.SaveChangesAsync(); await insert.Should().ThrowAsync<DbUpdateException>();
    }

    [Theory]
    [InlineData("CatalogSource")]
    [InlineData("CatalogVersion")]
    [InlineData("CatalogDigest")]
    [InlineData("CatalogEntryKey")]
    public async Task IncompletePlatformCatalogCannotPassANullSqliteCheck(string missing)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        await using var context = Context(connection); await context.Database.MigrateAsync();
        var inventory = Platform(Guid.NewGuid());
        if (missing == "CatalogSource") inventory.CatalogSource = null;
        if (missing == "CatalogVersion") inventory.CatalogVersion = null;
        if (missing == "CatalogDigest") inventory.CatalogDigest = null;
        if (missing == "CatalogEntryKey") inventory.CatalogEntryKey = null;
        context.Add(inventory); Func<Task> insert = () => context.SaveChangesAsync(); await insert.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task PlatformHasNoInventedTenantGuidAndNoPerWebMultiplication()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        await using var context = Context(connection); await context.Database.MigrateAsync();
        context.Add(Platform(Guid.NewGuid())); await context.SaveChangesAsync(); context.ChangeTracker.Clear();
        var row = await context.ClassicAspxInventories.SingleAsync(); row.SiteCollectionId.Should().BeNull(); row.FileUniqueId.Should().BeNull();
        row.AspxKey.Should().StartWith("platform:v1:"); (await context.ClassicAspxItemBindings.CountAsync()).Should().Be(0);
        row.CoverageState.Should().Be("Unknown");
    }

    [Fact]
    public void SchemaUsesApprovedKeysAndNullableSupplementaryItemGuid()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); using var context = Context(connection);
        var inventory = context.Model.FindEntityType(typeof(ClassicAspxInventory))!;
        inventory.GetTableName().Should().Be("ClassicAspxInventories"); inventory.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal("ScanId", "AspxKey");
        var binding = context.Model.FindEntityType(typeof(ClassicAspxItemBinding))!;
        binding.GetTableName().Should().Be("ClassicAspxItemBindings");
        binding.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal("ScanId", "AspxKey", "SiteCollectionId", "WebId", "ListId", "ItemId");
        binding.FindProperty(nameof(ClassicAspxItemBinding.ItemGuid))!.IsNullable.Should().BeTrue();
        binding.GetForeignKeys().Single().Properties.Select(p => p.Name).Should().Equal("ScanId", "AspxKey");
    }

    [Fact]
    public async Task ToolGeneratedMigrationUpgradeDowngradePreservesLegacyRowsWithoutBackfill()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); await connection.OpenAsync();
        await using var context = Context(connection); var migrations = context.Database.GetMigrations().ToArray();
        migrations.Last().Should().EndWith("_ClassicAspxInventoryBindings"); var prior = migrations[^2]; var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(prior); var scan = Guid.NewGuid();
        context.ClassicPages.Add(new ClassicPage { ScanId = scan, SiteUrl = "https://example.invalid/sites/legacy", WebUrl = "/", PageUrl = "/old.aspx", CustomizedPageStatus = "Uncustomized" });
        await context.SaveChangesAsync(); context.ChangeTracker.Clear(); await migrator.MigrateAsync(migrations.Last());
        (await context.ClassicPages.SingleAsync()).ScanId.Should().Be(scan);
        (await context.ClassicAspxInventories.CountAsync()).Should().Be(0); (await context.ClassicAspxItemBindings.CountAsync()).Should().Be(0);
        var inventory = Tenant(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); context.Add(inventory); context.Add(Binding(inventory, Guid.NewGuid(), Guid.NewGuid(), 1));
        await context.SaveChangesAsync(); context.ChangeTracker.Clear(); await migrator.MigrateAsync(prior); context.ChangeTracker.Clear();
        (await context.ClassicPages.SingleAsync()).CustomizedPageStatus.Should().Be("Uncustomized");
        await migrator.MigrateAsync(migrations.Last()); context.ChangeTracker.Clear();
        (await context.ClassicPages.CountAsync()).Should().Be(1); (await context.ClassicAspxInventories.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void GeneratedSnapshotHasNoPendingModelChanges()
    {
        using var connection = new SqliteConnection("Data Source=:memory:"); using var context = Context(connection);
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }
}
