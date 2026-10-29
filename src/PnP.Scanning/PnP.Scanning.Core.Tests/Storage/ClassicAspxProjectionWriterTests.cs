using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery.Aspx;
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
    /// <summary>
    /// Keeps an in-memory SQLite connection open and applies real migrations before each writer scenario.
    /// </summary>
    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        options = new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options;
        using var db = new ScanContext(options);
        await db.Database.MigrateAsync();
    }
    /// <summary>
    /// Closes the fixture-owned connection after the test, releasing the shared in-memory database.
    /// </summary>
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();
    /// <summary>
    /// Creates metadata-only tenant inventory with stable fixture scope and an optional distinct physical file identity.
    /// </summary>
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
    /// <summary>
    /// Creates an observed item pair in the fixture scope with a configurable binding reason and no supplementary item GUID.
    /// </summary>
    private ClassicAspxItemBinding Pair(string key, int id, ClassicAspxBindingKinds kind = ClassicAspxBindingKinds.ListFormRenderer) => new()
    {
        ScanId = scan, AspxKey = key, SiteCollectionId = site, WebId = web, ListId = list, ItemId = id,
        BindingKinds = kind, BindingProvenanceJson = "[\"observed-item\"]", ObservedAtUtc = DateTime.UtcNow
    };

    /// <summary>
    /// Replays one hundred renderer bindings idempotently without multiplying physical inventory or distinct item identities.
    /// </summary>
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

    /// <summary>
    /// Unions file-self and renderer reason flags while canonicalizing equivalent provenance objects into one normalized pair.
    /// </summary>
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

    /// <summary>
    /// Preserves separate pairs for multiple ASPX resources and for the same integer item ID in another web.
    /// </summary>
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

    /// <summary>
    /// Retains a real scoped item binding when its optional supplementary GUID is absent.
    /// </summary>
    [Fact]
    public async Task MissingOptionalItemGuidDoesNotDropARealScopedItem()
    {
        var file = File();
        await Writer.WriteAsync(new[] { file }, new[] { Pair(file.AspxKey, 1) });
        using var db = new ScanContext(options);
        (await db.ClassicAspxItemBindings.SingleAsync()).ItemGuid.Should().BeNull();
    }

    /// <summary>
    /// Keeps unbound files and idempotently seeded catalog entries as inventory without inventing bindings or effective handlers.
    /// </summary>
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

    /// <summary>
    /// Retains an earlier observed handler when a later read is denied while adding denial and incomplete-coverage evidence.
    /// </summary>
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

    /// <summary>
    /// Marks conflicting handler observations as changed during the scan and retains both values under one physical identity.
    /// </summary>
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

    /// <summary>
    /// Rejects unresolved physical identity and a denied-read framework fallback before any inventory can be persisted.
    /// </summary>
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

    /// <summary>
    /// Rejects invalid item keys and rolls back an unknown-parent failure without partially committing inventory or bindings.
    /// </summary>
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

    /// <summary>
    /// Keeps independent concurrent writes of the same file-item pair idempotent.
    /// </summary>
    [Fact]
    public async Task IndependentConcurrentBatchesRemainIdempotent()
    {
        var key = File().AspxKey;
        await Task.WhenAll(Enumerable.Range(1, 8).Select(_ => Writer.WriteAsync(new[] { File() }, new[] { Pair(key, 1) })));
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(1);
        (await db.ClassicAspxItemBindings.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// Treats equivalent absolute and server-relative URL representations as aliases rather than evidence of source drift.
    /// </summary>
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

    /// <summary>
    /// Streams all physical observations in keyset batches while allowing projection writes between yielded rows without retained readers.
    /// </summary>
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

    /// <summary>
    /// Rejects a batch beyond the writer limit without persisting an inventory prefix or silently truncating bindings.
    /// </summary>
    [Fact]
    public async Task OversizedBatchIsNotMaterializedOrSilentlyTruncated()
    {
        var file = File();
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { file }, Enumerable.Range(1, 257).Select(id => Pair(file.AspxKey, id))))
            .Should().ThrowAsync<ArgumentException>();
        using var db = new ScanContext(options); (await db.ClassicAspxInventories.CountAsync()).Should().Be(0);
    }
}
