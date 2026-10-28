using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery.M1;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

public sealed class ClassicAspxProjectionWriterTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private DbContextOptions<ScanContext> options;
    private readonly Guid scan = Guid.NewGuid();
    private readonly Guid site = Guid.NewGuid();
    private readonly Guid web = Guid.NewGuid();
    private readonly Guid list = Guid.NewGuid();
    private ClassicAspxProjectionWriter Writer => new(() => new ScanContext(options));
    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        options = new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options;
        using var db = new ScanContext(options);
        await db.Database.MigrateAsync();
    }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();
    private ClassicAspxInventory File(Guid? file = null) => new()
    {
        ScanId = scan, IdentityKind = ClassicAspxIdentityKinds.TenantFile,
        AspxKey = ClassicAspxInventory.CreateTenantFileKey(site, file ?? fileId),
        SiteCollectionId = site, FileUniqueId = file ?? fileId, WebId = web,
        Url = "/sites/test/renderer.aspx", FileReadState = "MetadataOnly",
        HandlerReadState = "NotRequested", HandlerParseState = "NotParsed",
        ProvenanceJson = "[\"file-listing\"]", CoverageState = "Unknown", ObservedAtUtc = DateTime.UtcNow
    };
    private readonly Guid fileId = Guid.NewGuid();
    private ClassicAspxItemBinding Pair(string key, int id, ClassicAspxBindingKinds kind = ClassicAspxBindingKinds.ListFormRenderer) => new()
    {
        ScanId = scan, AspxKey = key, SiteCollectionId = site, WebId = web, ListId = list, ItemId = id,
        BindingKinds = kind, BindingProvenanceJson = "[\"observed-item\"]", ObservedAtUtc = DateTime.UtcNow
    };

    [Fact]
    public async Task SharedRendererHasOneInventoryAndOneHundredDistinctItemsAfterReplay()
    {
        var file = File();
        await Writer.WriteAsync(new[] { file }, Enumerable.Range(1, 100).Select(id => Pair(file.AspxKey, id)));
        await Writer.WriteAsync(new[] { File() }, Enumerable.Range(1, 100).Select(id => Pair(file.AspxKey, id)));
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(1);
        (await db.ClassicAspxItemBindings.CountAsync()).Should().Be(100);
        (await db.ClassicAspxItemBindings.Select(x => x.ItemId).Distinct().CountAsync()).Should().Be(100);
    }

    [Fact]
    public async Task SameBackingAndRendererPairMergesReasonFlagsAndCanonicalProvenance()
    {
        var file = File();
        var backing = Pair(file.AspxKey, 1, ClassicAspxBindingKinds.FileSelf);
        backing.BindingProvenanceJson = "[{\"b\":2,\"a\":1}]";
        var renderer = Pair(file.AspxKey, 1, ClassicAspxBindingKinds.ContentTypeRenderer);
        renderer.BindingProvenanceJson = "[{\"a\":1,\"b\":2},\"renderer\"]";
        await Writer.WriteAsync(new[] { file }, new[] { backing, renderer });
        using var db = new ScanContext(options);
        var pair = await db.ClassicAspxItemBindings.SingleAsync();
        pair.BindingKinds.Should().Be(ClassicAspxBindingKinds.FileSelf | ClassicAspxBindingKinds.ContentTypeRenderer);
        using var json = JsonDocument.Parse(pair.BindingProvenanceJson);
        json.RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task SameItemCanBindMultipleAspxAndOtherWebKeysRemainDistinct()
    {
        var first = File(); var second = File(Guid.NewGuid());
        var otherWeb = Pair(first.AspxKey, 1); otherWeb.WebId = Guid.NewGuid();
        await Writer.WriteAsync(new[] { first, second }, new[] { Pair(first.AspxKey, 1), Pair(second.AspxKey, 1), otherWeb });
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(2);
        (await db.ClassicAspxItemBindings.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task MissingOptionalItemGuidDoesNotDropARealScopedItem()
    {
        var file = File();
        await Writer.WriteAsync(new[] { file }, new[] { Pair(file.AspxKey, 1) });
        using var db = new ScanContext(options);
        (await db.ClassicAspxItemBindings.SingleAsync()).ItemGuid.Should().BeNull();
    }

    [Fact]
    public async Task UnboundAndCatalogMembersRemainInventoryWithoutFabricatedBindings()
    {
        await Writer.WriteAsync(new[] { File() }, Array.Empty<ClassicAspxItemBinding>());
        var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":\"platform-aspx-catalog/v1\",\"catalogVersion\":\"test-v1\",\"source\":\"unit\",\"entries\":[{\"id\":\"orphan\",\"deploymentIdentity\":\"hive16-synthetic\",\"culture\":\"neutral\",\"path\":\"/_layouts/15/orphan.aspx\"}]}");
        var catalog = PlatformAspxCatalog.Parse(bytes);
        await Writer.SeedCatalogAsync(scan, catalog);
        await Writer.SeedCatalogAsync(scan, catalog);
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(2);
        (await db.ClassicAspxItemBindings.CountAsync()).Should().Be(0);
        var member = await db.ClassicAspxInventories.SingleAsync(x => x.IdentityKind == ClassicAspxIdentityKinds.PlatformCatalog);
        member.WebId.Should().BeNull(); member.SiteCollectionId.Should().BeNull();
        member.HandlerEffectiveType.Should().BeNull();
        member.CoverageState.Should().Be("Unknown");
    }

    [Fact]
    public async Task DeniedReadDoesNotErasePreviouslyObservedHandlerFacts()
    {
        var file = File();
        file.HandlerReadState = "Available"; file.HandlerParseState = "Declared"; file.HandlerSource = "ActualFileBytes";
        file.HandlerDeclaredType = "Observed.Handler, SourceAssembly"; file.HandlerNormalizedType = file.HandlerDeclaredType;
        file.HandlerEffectiveType = file.HandlerDeclaredType;
        await Writer.WriteAsync(new[] { file }, Array.Empty<ClassicAspxItemBinding>());
        var denied = File(); denied.HandlerReadState = "Denied"; denied.HandlerParseState = "NotParsed";
        denied.HandlerErrorCodes = "HTTP403"; denied.CoverageState = "Incomplete";
        await Writer.WriteAsync(new[] { denied }, Array.Empty<ClassicAspxItemBinding>());
        using var db = new ScanContext(options);
        var row = await db.ClassicAspxInventories.SingleAsync();
        row.HandlerEffectiveType.Should().Be("Observed.Handler, SourceAssembly");
        row.HandlerErrorCodes.Should().Contain("HTTP403");
        row.CoverageState.Should().Be("Incomplete");
    }

    [Fact]
    public async Task HandlerChangeRetainsChangeEvidenceInsteadOfCreatingAnotherIdentity()
    {
        var first = File(); first.HandlerReadState = "Available"; first.HandlerParseState = "Declared";
        first.HandlerSource = "ActualFileBytes"; first.HandlerEffectiveType = first.HandlerDeclaredType = "First.Handler";
        var next = File(); next.HandlerReadState = "Available"; next.HandlerParseState = "Declared";
        next.HandlerSource = "ActualFileBytes"; next.HandlerEffectiveType = next.HandlerDeclaredType = "Second.Handler";
        await Writer.WriteAsync(new[] { first }, Array.Empty<ClassicAspxItemBinding>());
        await Writer.WriteAsync(new[] { next }, Array.Empty<ClassicAspxItemBinding>());
        using var db = new ScanContext(options);
        var row = await db.ClassicAspxInventories.SingleAsync();
        row.CoverageState.Should().Be("ChangedDuringScan");
        row.ProvenanceJson.Should().Contain("First.Handler").And.Contain("Second.Handler");
    }

    [Fact]
    public async Task UnresolvedIdentityAndInventedDefaultAreRejectedBeforeWriting()
    {
        var file = File(); file.FileUniqueId = null;
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { file }, Array.Empty<ClassicAspxItemBinding>())).Should().ThrowAsync<ArgumentException>();
        var denied = File(); denied.HandlerReadState = "Denied"; denied.HandlerSource = "ActualFileBytes";
        denied.HandlerParseState = "NoInherits"; denied.HandlerEffectiveType = "System.Web.UI.Page";
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { denied }, Array.Empty<ClassicAspxItemBinding>())).Should().ThrowAsync<ArgumentException>();
        using var db = new ScanContext(options); (await db.ClassicAspxInventories.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task InvalidItemAndUnknownParentCauseNoPartialCommit()
    {
        var file = File(); var invalid = Pair(file.AspxKey, 0);
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { file }, new[] { invalid })).Should().ThrowAsync<ArgumentException>();
        var unknown = Pair("tenant:v1:unresolved-parent", 1);
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { File() }, new[] { unknown })).Should().ThrowAsync<DbUpdateException>();
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(0);
        (await db.ClassicAspxItemBindings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task IndependentConcurrentBatchesRemainIdempotent()
    {
        var key = File().AspxKey;
        await Task.WhenAll(Enumerable.Range(1, 8).Select(_ => Writer.WriteAsync(new[] { File() }, new[] { Pair(key, 1) })));
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(1);
        (await db.ClassicAspxItemBindings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task UrlRepresentationAliasesDoNotInventSourceDrift()
    {
        var first = File(); first.Url = "https://example.invalid/sites/test/My%20Page.aspx";
        var alias = File(); alias.Url = "/sites/test/My Page.aspx";
        await Writer.WriteAsync(new[] { first }, Array.Empty<ClassicAspxItemBinding>());
        await Writer.WriteAsync(new[] { alias }, Array.Empty<ClassicAspxItemBinding>());
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.SingleAsync()).CoverageState.Should().NotBe("ChangedDuringScan");
    }

    [Fact]
    public async Task PhysicalStreamUsesBoundedKeysetBatchesAndReleasesReadersBeforePublishing()
    {
        var source = new AssessmentDiscoveryWriter(() => new ScanContext(options));
        await source.WriteAsync(Enumerable.Range(1, 300).Select(id => new ClassicPageDiscovery
        {
            ScanId = scan, RecordKey = "page:" + id.ToString("D8"), RowType = "Page", ScopeType = "File",
            SiteUrl = "https://example.invalid/sites/test", WebUrl = "/", Url = "/sites/test/renderer.aspx",
            DiscoveryStatus = "Discovered", ObservedAtUtc = DateTime.UtcNow
        }));
        var count = 0;
        await foreach (var _ in source.ReadPageStreamAsync(scan, "https://example.invalid/sites/test", "/"))
        {
            await Writer.WriteAsync(new[] { File() }, Array.Empty<ClassicAspxItemBinding>());
            count++;
        }
        count.Should().Be(300);
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task OversizedBatchIsNotMaterializedOrSilentlyTruncated()
    {
        var file = File();
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { file }, Enumerable.Range(1, 257).Select(id => Pair(file.AspxKey, id))))
            .Should().ThrowAsync<ArgumentException>();
        using var db = new ScanContext(options); (await db.ClassicAspxInventories.CountAsync()).Should().Be(0);
    }
}
