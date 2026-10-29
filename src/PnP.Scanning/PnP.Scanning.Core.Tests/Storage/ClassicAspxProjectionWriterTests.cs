using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Discovery.Aspx;
using PnP.Scanning.Core.Storage;
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
    private readonly Guid folder = Guid.NewGuid();
    private readonly Guid fileId = Guid.NewGuid();
    private readonly DateTime observed = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private ClassicAspxProjectionWriter Writer => new(() => new ScanContext(options));

    /// <summary>Keeps a private in-memory SQLite connection open and applies the generated migrations for each writer scenario.</summary>
    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        options = new DbContextOptionsBuilder<ScanContext>().UseSqlite(connection).Options;
        using var db = new ScanContext(options);
        await db.Database.MigrateAsync();
    }

    /// <summary>Closes only this test's connection and releases its isolated in-memory database.</summary>
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();

    /// <summary>Creates a metadata observation with a stable real physical identity and independent owner slots.</summary>
    private ClassicAspxInventory File(Guid? file = null) => new()
    {
        ScanId = scan,
        AspxKey = ClassicAspxInventory.CreateTenantFileKey(site, file ?? fileId),
        SiteCollectionId = site,
        FileUniqueId = file ?? fileId,
        WebId = web,
        ListId = list,
        FolderUniqueId = folder,
        Url = "/sites/test/renderer.aspx",
        FileName = "renderer.aspx",
        FileReadState = "Available",
        ProvenanceJson = "[\"file-listing\"]",
        CoverageState = "Observed",
        ObservedAtUtc = observed
    };

    /// <summary>Replays the same physical observation without multiplying files or changing the earliest and latest observation bounds.</summary>
    [Fact]
    public async Task ReplayedInventoryIsIdempotent()
    {
        await Writer.WriteAsync(new[] { File() });
        await Writer.WriteAsync(new[] { File() });
        using var db = new ScanContext(options);
        var row = await db.ClassicAspxInventories.SingleAsync();
        row.AspxKey.Should().Be(File().AspxKey);
        row.FirstObservedAtUtc.Should().Be(observed);
        row.ObservedAtUtc.Should().Be(observed);
        row.ProvenanceJson.Should().Be("[\"file-listing\"]");
        row.CoverageState.Should().Be("Observed");
        row.FileErrorCodes.Should().BeNull();
        row.FileErrorDetail.Should().BeNull();
    }

    /// <summary>Unions equivalent JSON objects and distinct observation sources even when duplicates appear in one transactional batch.</summary>
    [Fact]
    public async Task SameBatchDuplicatesCanonicalizeAndUnionProvenance()
    {
        var first = File();
        first.ProvenanceJson = "[{\"b\":2,\"a\":1}]";
        var second = File();
        second.ProvenanceJson = "[{\"a\":1,\"b\":2},\"other-folder-entry\"]";
        await Writer.WriteAsync(new[] { first, second });
        using var db = new ScanContext(options);
        var row = await db.ClassicAspxInventories.SingleAsync();
        using var json = JsonDocument.Parse(row.ProvenanceJson);
        json.RootElement.GetArrayLength().Should().Be(2);
        row.ProvenanceJson.Should().Contain("other-folder-entry");
    }

    /// <summary>Keeps independent file GUIDs distinct even when their paths are equal and no backing item identity exists.</summary>
    [Fact]
    public async Task DifferentPhysicalFilesDoNotCollapseByPath()
    {
        await Writer.WriteAsync(new[] { File(), File(Guid.NewGuid()) });
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(2);
    }

    /// <summary>Preserves acquired metadata when a later incomplete observation omits optional ownership and nullable values.</summary>
    [Fact]
    public async Task MissingMetadataDoesNotEraseEarlierFacts()
    {
        var first = File();
        first.ContentTypeId = "0x0101";
        first.CustomizedPageStatus = "Customized";
        first.SetupPath = "features/template.aspx";
        await Writer.WriteAsync(new[] { first });
        var sparse = File();
        sparse.WebId = null;
        sparse.ListId = null;
        sparse.FolderUniqueId = null;
        sparse.FileName = null;
        sparse.Url = null;
        sparse.FileReadState = "NotRequested";
        sparse.CoverageState = "Incomplete";
        await Writer.WriteAsync(new[] { sparse });
        using var db = new ScanContext(options);
        var row = await db.ClassicAspxInventories.SingleAsync();
        row.WebId.Should().Be(web);
        row.ListId.Should().Be(list);
        row.FolderUniqueId.Should().Be(folder);
        row.FileName.Should().Be("renderer.aspx");
        row.Url.Should().Be("/sites/test/renderer.aspx");
        row.ContentTypeId.Should().Be("0x0101");
        row.CustomizedPageStatus.Should().Be("Customized");
        row.SetupPath.Should().Be("features/template.aspx");
        row.FileReadState.Should().Be("Available");
        row.CoverageState.Should().Be("Incomplete");
    }

    /// <summary>Records conflicting observation ownership and metadata without changing physical identity or erasing either conflicting fact.</summary>
    [Fact]
    public async Task MetadataChangesRetainBeforeAndAfterUnderOnePhysicalKey()
    {
        var first = File();
        first.ContentTypeId = "0x0101";
        await Writer.WriteAsync(new[] { first });
        var next = File();
        next.ListId = Guid.NewGuid();
        next.FolderUniqueId = Guid.NewGuid();
        next.ContentTypeId = "0x010109";
        next.Url = "/sites/test/renamed.aspx";
        next.ObservedAtUtc = observed.AddMinutes(2);
        await Writer.WriteAsync(new[] { next });
        using var db = new ScanContext(options);
        var row = await db.ClassicAspxInventories.SingleAsync();
        row.AspxKey.Should().Be(first.AspxKey);
        row.CoverageState.Should().Be("ChangedDuringScan");
        row.ListId.Should().Be(next.ListId);
        row.FolderUniqueId.Should().Be(next.FolderUniqueId);
        row.ProvenanceJson.Should().Contain("0x0101").And.Contain("0x010109")
            .And.Contain("renderer.aspx").And.Contain("renamed.aspx")
            .And.Contain(list.ToString()).And.Contain(next.ListId!.Value.ToString());
        row.FirstObservedAtUtc.Should().Be(observed);
        row.ObservedAtUtc.Should().Be(observed.AddMinutes(2));
    }

    /// <summary>Retains successful metadata facts and denial evidence together instead of replacing acquired facts with a read failure.</summary>
    [Fact]
    public async Task LaterDeniedReadPreservesSuccessfulMetadataAndItsFailureEvidence()
    {
        await Writer.WriteAsync(new[] { File() });
        var denied = File();
        denied.FileReadState = "Unavailable";
        denied.FileErrorCodes = "HTTP403";
        denied.FileErrorDetail = "metadata denied";
        denied.CoverageState = "Incomplete";
        await Writer.WriteAsync(new[] { denied });
        await Writer.WriteAsync(new[] { denied });
        using var db = new ScanContext(options);
        var row = await db.ClassicAspxInventories.SingleAsync();
        row.FileReadState.Should().Be("Available");
        row.FileErrorCodes.Should().Be("HTTP403");
        row.FileErrorDetail.Should().Be("metadata denied");
        row.CoverageState.Should().Be("ChangedDuringScan");
        row.ProvenanceJson.Should().Contain("Available").And.Contain("Unavailable");
    }

    /// <summary>Retains distinct error blocks without duplicating replayed observations or collapsing intentional repeated lines inside an error detail.</summary>
    [Fact]
    public async Task ErrorBlocksUnionWithoutErasingMultilineContent()
    {
        var first = File();
        first.FileErrorCodes = "HTTP403";
        first.FileErrorDetail = "first line\nrepeated line\nrepeated line";
        var second = File();
        second.FileErrorCodes = "HTTP404";
        second.FileErrorDetail = "other error";
        await Writer.WriteAsync(new[] { first });
        await Writer.WriteAsync(new[] { second });
        await Writer.WriteAsync(new[] { FileWithErrors(first) });
        await Writer.WriteAsync(new[] { FileWithErrors(second) });
        using var db = new ScanContext(options);
        var row = await db.ClassicAspxInventories.SingleAsync();
        row.FileErrorCodes.Should().Be("HTTP403\nHTTP404");
        row.FileErrorDetail.Should().Be("first line\nrepeated line\nrepeated line\nother error");
    }

    /// <summary>Copies only the observed errors onto a fresh physical observation so replay does not rely on a previously tracked EF entity.</summary>
    private ClassicAspxInventory FileWithErrors(ClassicAspxInventory source)
    {
        var file = File();
        file.FileErrorCodes = source.FileErrorCodes;
        file.FileErrorDetail = source.FileErrorDetail;
        return file;
    }

    /// <summary>Preserves earliest and latest timestamps even when observations are replayed out of chronological order.</summary>
    [Fact]
    public async Task TimeBoundsDoNotDependOnReplayOrder()
    {
        var latest = File();
        latest.ObservedAtUtc = observed.AddHours(2);
        var earlier = File();
        earlier.FirstObservedAtUtc = observed.AddMinutes(-3);
        await Writer.WriteAsync(new[] { latest });
        await Writer.WriteAsync(new[] { earlier });
        using var db = new ScanContext(options);
        var row = await db.ClassicAspxInventories.SingleAsync();
        row.FirstObservedAtUtc.Should().Be(observed.AddMinutes(-3));
        row.ObservedAtUtc.Should().Be(observed.AddHours(2));
    }

    /// <summary>Rejects absent physical GUIDs, mismatched keys, placeholder ownership, and non-array provenance before committing any prefix.</summary>
    [Theory]
    [InlineData("SiteCollectionId")]
    [InlineData("FileUniqueId")]
    [InlineData("ScanId")]
    [InlineData("AspxKey")]
    [InlineData("WebId")]
    [InlineData("ListId")]
    [InlineData("FolderUniqueId")]
    [InlineData("ProvenanceJson")]
    public async Task InvalidObservationRejectsWholeBatch(string field)
    {
        var invalid = File(Guid.NewGuid());
        switch (field)
        {
            case "SiteCollectionId":
                invalid.SiteCollectionId = Guid.Empty;
                break;
            case "FileUniqueId":
                invalid.FileUniqueId = Guid.Empty;
                break;
            case "ScanId":
                invalid.ScanId = Guid.Empty;
                break;
            case "AspxKey":
                invalid.AspxKey = "tenant:v1:path-invented";
                break;
            case "WebId":
                invalid.WebId = Guid.Empty;
                break;
            case "ListId":
                invalid.ListId = Guid.Empty;
                break;
            case "FolderUniqueId":
                invalid.FolderUniqueId = Guid.Empty;
                break;
            case "ProvenanceJson":
                invalid.ProvenanceJson = "{}";
                break;
        }
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { File(), invalid })).Should().ThrowAsync<ArgumentException>();
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(0);
    }

    /// <summary>Rejects a mixed-assessment batch rather than allowing otherwise valid observations to cross scan boundaries.</summary>
    [Fact]
    public async Task MixedScansCannotPartiallyCommit()
    {
        var other = File(Guid.NewGuid());
        other.ScanId = Guid.NewGuid();
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { File(), other })).Should().ThrowAsync<ArgumentException>();
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(0);
    }

    /// <summary>Propagates a real storage constraint failure and rolls back valid observations in the same transaction.</summary>
    [Fact]
    public async Task StorageFailureRollsBackTheEntireBatch()
    {
        var invalid = File(Guid.NewGuid());
        invalid.FileReadState = null;
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { File(), invalid })).Should().ThrowAsync<DbUpdateException>();
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(0);
    }

    /// <summary>Serializes independent concurrent batches so replay of the same physical file remains one row.</summary>
    [Fact]
    public async Task IndependentConcurrentBatchesRemainIdempotent()
    {
        await Task.WhenAll(Enumerable.Range(1, 8).Select(_ => Writer.WriteAsync(new[] { File() })));
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(1);
    }

    /// <summary>Treats equivalent encoded, absolute, and server-relative URL representations as locators, not new files or metadata drift.</summary>
    [Fact]
    public async Task UrlRepresentationAliasesDoNotInventDrift()
    {
        var first = File();
        first.Url = "https://example.invalid/sites/test/My%20Page.aspx";
        var alias = File();
        alias.Url = "/sites/test/My Page.aspx?ignored=true";
        await Writer.WriteAsync(new[] { first });
        await Writer.WriteAsync(new[] { alias });
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.SingleAsync()).CoverageState.Should().Be("Observed");
    }

    /// <summary>Allows keyset page streaming and projection writes to interleave without retaining a SQLite reader between published observations.</summary>
    [Fact]
    public async Task PhysicalStreamReleasesReadersBeforePublishing()
    {
        var source = new AssessmentDiscoveryWriter(() => new ScanContext(options));
        await source.WriteAsync(Enumerable.Range(1, 300).Select(id => new ClassicPageDiscovery
        {
            ScanId = scan,
            RecordKey = "page:" + id.ToString("D8"),
            RowType = "Page",
            ScopeType = "File",
            SiteUrl = "https://example.invalid/sites/test",
            WebUrl = "/",
            Url = "/sites/test/renderer.aspx",
            DiscoveryStatus = "Discovered",
            ObservedAtUtc = observed
        }));
        var count = 0;
        await foreach (var _ in source.ReadPageStreamAsync(scan, "https://example.invalid/sites/test", "/"))
        {
            await Writer.WriteAsync(new[] { File() });
            count++;
        }
        count.Should().Be(300);
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(1);
    }

    /// <summary>Reads only one bounded batch plus a sentinel and never opens a context for oversized inventory input.</summary>
    [Fact]
    public async Task OversizedBatchStopsEnumerationBeforeContextCreation()
    {
        var enumerated = 0;
        var writer = new ClassicAspxProjectionWriter(() => throw new InvalidOperationException("No context should be opened."));
        var rows = Enumerable.Range(1, 1000).Select(_ =>
        {
            enumerated++;
            return File(Guid.NewGuid());
        });
        await FluentActions.Awaiting(() => writer.WriteAsync(rows)).Should().ThrowAsync<ArgumentException>();
        enumerated.Should().Be(257);
    }

    /// <summary>Persists the full permitted batch rather than silently dropping the final allowed physical file.</summary>
    [Fact]
    public async Task MaximumBatchIsNotTruncated()
    {
        await Writer.WriteAsync(Enumerable.Range(1, 256).Select(_ => File(Guid.NewGuid())));
        using var db = new ScanContext(options);
        (await db.ClassicAspxInventories.CountAsync()).Should().Be(256);
    }

    /// <summary>Skips empty input without creating a context and honors cancellation before a nonempty batch can enter storage.</summary>
    [Fact]
    public async Task EmptyAndCanceledBatchesDoNotOpenContexts()
    {
        var writer = new ClassicAspxProjectionWriter(() => throw new InvalidOperationException("No context should be opened."));
        await writer.WriteAsync(Array.Empty<ClassicAspxInventory>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await FluentActions.Awaiting(() => writer.WriteAsync(new[] { File() }, cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Retains incomplete and changed coverage precedence without interpreting observed inventory as complete tenant coverage.</summary>
    [Theory]
    [InlineData("Observed", "Unknown", "Observed")]
    [InlineData("Observed", "Incomplete", "Incomplete")]
    [InlineData("Incomplete", "Observed", "Incomplete")]
    [InlineData("ChangedDuringScan", "Incomplete", "ChangedDuringScan")]
    public void CoverageMergePreservesEvidenceLimitations(string left, string right, string expected)
    {
        ClassicAspxProjectionWriter.MergeCoverage(left, right).Should().Be(expected);
    }
}
