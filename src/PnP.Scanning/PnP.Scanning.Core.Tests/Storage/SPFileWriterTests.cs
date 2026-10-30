using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

public sealed class SPFileWriterTests : IAsyncLifetime
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
    private SPFileWriter Writer => new(() => new ScanContext(options));

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
    private SPFile File(Guid? file = null) => new()
    {
        ScanId = scan,
        SiteCollectionId = site,
        FileUniqueId = file ?? fileId,
        WebId = web,
        ListId = list,
        FolderUniqueId = folder,
        ServerRelativeUrl = "/sites/test/renderer.aspx",
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
        var row = await db.SPFiles.SingleAsync();
        row.SiteCollectionId.Should().Be(site);
        row.FileUniqueId.Should().Be(fileId);
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
        var row = await db.SPFiles.SingleAsync();
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
        (await db.SPFiles.CountAsync()).Should().Be(2);
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
        sparse.ServerRelativeUrl = null;
        sparse.FileReadState = "NotRequested";
        sparse.CoverageState = "Incomplete";
        await Writer.WriteAsync(new[] { sparse });
        using var db = new ScanContext(options);
        var row = await db.SPFiles.SingleAsync();
        row.WebId.Should().Be(web);
        row.ListId.Should().Be(list);
        row.FolderUniqueId.Should().Be(folder);
        row.FileName.Should().Be("renderer.aspx");
        row.ServerRelativeUrl.Should().Be("/sites/test/renderer.aspx");
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
        next.ServerRelativeUrl = "/sites/test/renamed.aspx";
        next.ObservedAtUtc = observed.AddMinutes(2);
        await Writer.WriteAsync(new[] { next });
        using var db = new ScanContext(options);
        var row = await db.SPFiles.SingleAsync();
        row.SiteCollectionId.Should().Be(site);
        row.FileUniqueId.Should().Be(fileId);
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
        var row = await db.SPFiles.SingleAsync();
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
        var row = await db.SPFiles.SingleAsync();
        row.FileErrorCodes.Should().Be("HTTP403\nHTTP404");
        row.FileErrorDetail.Should().Be("first line\nrepeated line\nrepeated line\nother error");
    }

    /// <summary>Copies only the observed errors onto a fresh physical observation so replay does not rely on a previously tracked EF entity.</summary>
    private SPFile FileWithErrors(SPFile source)
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
        var row = await db.SPFiles.SingleAsync();
        row.FirstObservedAtUtc.Should().Be(observed.AddMinutes(-3));
        row.ObservedAtUtc.Should().Be(observed.AddHours(2));
    }

    /// <summary>Rejects absent physical GUIDs, placeholder ownership, and malformed evidence/state contracts before committing any prefix.</summary>
    [Theory]
    [InlineData("SiteCollectionId")]
    [InlineData("FileUniqueId")]
    [InlineData("ScanId")]
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
        (await db.SPFiles.CountAsync()).Should().Be(0);
    }

    /// <summary>Rejects a mixed-assessment batch rather than allowing otherwise valid observations to cross scan boundaries.</summary>
    [Fact]
    public async Task MixedScansCannotPartiallyCommit()
    {
        var other = File(Guid.NewGuid());
        other.ScanId = Guid.NewGuid();
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { File(), other })).Should().ThrowAsync<ArgumentException>();
        using var db = new ScanContext(options);
        (await db.SPFiles.CountAsync()).Should().Be(0);
    }

    /// <summary>Propagates a real storage constraint failure and rolls back valid observations in the same transaction.</summary>
    [Fact]
    public async Task StorageFailureRollsBackTheEntireBatch()
    {
        var invalid = File(Guid.NewGuid());
        invalid.FileReadState = null;
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { File(), invalid })).Should().ThrowAsync<DbUpdateException>();
        using var db = new ScanContext(options);
        (await db.SPFiles.CountAsync()).Should().Be(0);
    }

    /// <summary>Serializes independent concurrent batches so replay of the same physical file remains one row.</summary>
    [Fact]
    public async Task IndependentConcurrentBatchesRemainIdempotent()
    {
        await Task.WhenAll(Enumerable.Range(1, 8).Select(_ => Writer.WriteAsync(new[] { File() })));
        using var db = new ScanContext(options);
        (await db.SPFiles.CountAsync()).Should().Be(1);
    }

    /// <summary>Retains decoded physical path characters exactly instead of treating percent sequences or fragment/query characters as URL syntax.</summary>
    [Fact]
    public async Task RawServerRelativePathIsNeitherDecodedNorCanonicalized()
    {
        var first = File();
        first.ServerRelativeUrl = "/sites/test/100%25?# Page.aspx";
        await Writer.WriteAsync(new[] { first });
        using (var db = new ScanContext(options))
        {
            (await db.SPFiles.SingleAsync()).ServerRelativeUrl.Should().Be(first.ServerRelativeUrl);
        }
        var next = File();
        next.ServerRelativeUrl = "/sites/test/100%?# Page.aspx";
        await Writer.WriteAsync(new[] { next });
        using var retained = new ScanContext(options);
        var row = await retained.SPFiles.SingleAsync();
        row.ServerRelativeUrl.Should().Be(next.ServerRelativeUrl);
        row.CoverageState.Should().Be("ChangedDuringScan");
        row.ProvenanceJson.Should().Contain("100%25?# Page.aspx").And.Contain("100%?# Page.aspx");
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
        (await db.SPFiles.CountAsync()).Should().Be(1);
    }

    /// <summary>Reads only one bounded batch plus a sentinel and never opens a context for oversized inventory input.</summary>
    [Fact]
    public async Task OversizedBatchStopsEnumerationBeforeContextCreation()
    {
        var enumerated = 0;
        var writer = new SPFileWriter(() => throw new InvalidOperationException("No context should be opened."));
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
        (await db.SPFiles.CountAsync()).Should().Be(256);
    }

    /// <summary>Skips empty input without creating a context and honors cancellation before a nonempty batch can enter storage.</summary>
    [Fact]
    public async Task EmptyAndCanceledBatchesDoNotOpenContexts()
    {
        var writer = new SPFileWriter(() => throw new InvalidOperationException("No context should be opened."));
        await writer.WriteAsync(Array.Empty<SPFile>());
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
        SPFileWriter.MergeCoverage(left, right).Should().Be(expected);
    }

    /// <summary>Fails closed on a previously owned physical identity and rolls back a valid prefix from the other scan.</summary>
    [Fact]
    public async Task ExistingPhysicalFileCannotBeReassignedAcrossScans()
    {
        var original = File();
        await Writer.WriteAsync(new[] { original });
        var otherScan = Guid.NewGuid();
        var prefix = File(Guid.NewGuid());
        prefix.ScanId = otherScan;
        var collision = File();
        collision.ScanId = otherScan;
        collision.FileName = "reassigned.aspx";
        collision.ProvenanceJson = "[\"must-not-commit\"]";
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { prefix, collision }))
            .Should().ThrowAsync<InvalidOperationException>();
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.ScanId.Should().Be(scan);
        saved.FileName.Should().Be(original.FileName);
        saved.ProvenanceJson.Should().Be(original.ProvenanceJson);
    }

    /// <summary>Scopes the file GUID to its real site collection while treating the scan as ordinary ownership metadata.</summary>
    [Fact]
    public async Task SameFileGuidInDifferentSitesRemainsDistinct()
    {
        var otherSite = File();
        otherSite.SiteCollectionId = Guid.NewGuid();
        await Writer.WriteAsync(new[] { File(), otherSite });
        using var db = new ScanContext(options);
        (await db.SPFiles.CountAsync()).Should().Be(2);
    }

    /// <summary>Does not turn absent NoExecute observations into a numeric zero, a poison decision, or a full document-flags mask.</summary>
    [Fact]
    public async Task MissingRawNoExecuteRemainsMissing()
    {
        await Writer.WriteAsync(new[] { File() });
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.NoExecuteRaw.Should().BeNull();
        saved.NoExecuteSource.Should().BeNull();
        saved.NoExecuteReadState.Should().Be("NotAttempted");
    }

    /// <summary>Stores original NoExecute field text, including 0/1, without parsing, trimming, padding, or expanding it into a server document-flags mask.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("0001")]
    [InlineData(" 1 ")]
    [InlineData("")]
    [InlineData("uninterpreted NoExecute field text")]
    public async Task RawNoExecuteIsStoredVerbatim(string raw)
    {
        var observation = File();
        observation.NoExecuteRaw = raw;
        observation.NoExecuteSource = "synthetic-noexecute-source";
        observation.NoExecuteReadState = "Available";
        await Writer.WriteAsync(new[] { observation });
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.NoExecuteRaw.Should().Be(raw);
        saved.NoExecuteSource.Should().Be("synthetic-noexecute-source");
        saved.NoExecuteReadState.Should().Be("Available");
    }

    /// <summary>Preserves all newly added raw metadata and nullable scalar facts when a later incomplete read omits them.</summary>
    [Fact]
    public async Task SparseReadsDoNotEraseItemAndRawNoExecuteFacts()
    {
        var first = File();
        first.ListItemId = 42;
        first.Length = 1234567890123;
        first.ClientSideApplicationId = "00000000-0000-0000-0000-000000000000";
        first.HtmlFileType = "";
        first.FileType = "aspx";
        first.BSN = "raw-bsn";
        first.WikiFieldHasValue = false;
        first.ItemMetadataReadState = "Available";
        first.ItemMetadataFieldStatesJson = "{\"WikiField\":\"value\",\"BSN\":\"value\"}";
        first.NoExecuteRaw = "1";
        first.NoExecuteSource = "synthetic-noexecute-source";
        first.NoExecuteReadState = "Available";
        await Writer.WriteAsync(new[] { first });
        var sparse = File();
        sparse.ItemMetadataFieldStatesJson = "{\"WikiField\":\"omitted\",\"new-field\":\"null\"}";
        sparse.ItemMetadataReadState = "Unavailable";
        sparse.NoExecuteReadState = "Unavailable";
        await Writer.WriteAsync(new[] { sparse });
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.ListItemId.Should().Be(42);
        saved.Length.Should().Be(1234567890123);
        saved.ClientSideApplicationId.Should().Be(first.ClientSideApplicationId);
        saved.HtmlFileType.Should().BeEmpty();
        saved.FileType.Should().Be("aspx");
        saved.BSN.Should().Be("raw-bsn");
        saved.WikiFieldHasValue.Should().BeFalse();
        saved.NoExecuteRaw.Should().Be("1");
        saved.NoExecuteSource.Should().Be("synthetic-noexecute-source");
        saved.ItemMetadataReadState.Should().Be("Available");
        saved.NoExecuteReadState.Should().Be("Available");
        using var states = JsonDocument.Parse(saved.ItemMetadataFieldStatesJson);
        states.RootElement.GetProperty("WikiField").GetString().Should().Be("value");
        states.RootElement.GetProperty("new-field").GetString().Should().Be("null");
        saved.ProvenanceJson.Should().Contain("ItemMetadataReadState").And.Contain("NoExecuteReadState");
    }

    /// <summary>Records changed nullable scalar and raw string facts without creating another physical file or dropping old evidence.</summary>
    [Fact]
    public async Task NewRawMetadataConflictsRetainBothValues()
    {
        var first = File();
        first.ListItemId = 42;
        first.Length = 100;
        first.WikiFieldHasValue = false;
        first.NoExecuteRaw = "0";
        first.NoExecuteSource = "source-one";
        first.NoExecuteReadState = "Available";
        first.ClientSideApplicationId = "app-one";
        first.HtmlFileType = "type-one";
        first.FileType = "file-one";
        first.BSN = "bsn-one";
        first.ItemMetadataFieldStatesJson = "{\"WikiField\":\"value\",\"other-field\":\"value\"}";
        var next = File();
        next.ListItemId = 84;
        next.Length = 200;
        next.WikiFieldHasValue = true;
        next.NoExecuteRaw = "1";
        next.NoExecuteSource = "source-two";
        next.NoExecuteReadState = "Available";
        next.ClientSideApplicationId = "app-two";
        next.HtmlFileType = "type-two";
        next.FileType = "file-two";
        next.BSN = "bsn-two";
        next.ItemMetadataFieldStatesJson = "{\"WikiField\":\"value\",\"other-field\":\"null\"}";
        await Writer.WriteAsync(new[] { first, next });
        await Writer.WriteAsync(new[] { next });
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.ListItemId.Should().Be(84);
        saved.Length.Should().Be(200);
        saved.WikiFieldHasValue.Should().BeTrue();
        saved.NoExecuteRaw.Should().Be("1");
        saved.CoverageState.Should().Be("ChangedDuringScan");
        foreach (var field in new[] { "ListItemId", "Length", "WikiFieldHasValue", "NoExecuteRaw", "NoExecuteSource", "ClientSideApplicationId", "HtmlFileType", "FileType", "BSN", "ItemMetadataFieldStatesJson.other-field" })
        {
            saved.ProvenanceJson.Should().Contain(field);
        }
        saved.ProvenanceJson.Should().Contain("bsn-one").And.Contain("bsn-two");
        using var evidence = JsonDocument.Parse(saved.ProvenanceJson);
        evidence.RootElement.GetArrayLength().Should().Be(11);
        var rawConflict = evidence.RootElement.EnumerateArray().Single(entry =>
            entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("field", out var field) &&
            field.GetString() == nameof(SPFile.NoExecuteRaw));
        rawConflict.GetProperty("before").GetString().Should().Be("0");
        rawConflict.GetProperty("after").GetString().Should().Be("1");
    }

    /// <summary>Rejects malformed field-state shapes and ambiguous duplicate names before a valid batch prefix reaches storage.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("{\"WikiField\":null}")]
    [InlineData("{\"WikiField\":\"Available\"}")]
    [InlineData("{\"WikiField\":true}")]
    [InlineData("{\"\":\"value\"}")]
    [InlineData("{\"WikiField\":\"value\",\"WikiField\":\"null\"}")]
    public async Task InvalidFieldStatesRejectWholeBatch(string states)
    {
        var invalid = File(Guid.NewGuid());
        invalid.ItemMetadataFieldStatesJson = states;
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { File(), invalid }))
            .Should().ThrowAsync<ArgumentException>();
        using var db = new ScanContext(options);
        (await db.SPFiles.CountAsync()).Should().Be(0);
    }
    /// <summary>Clears returned null text and records the former facts while treating an explicitly null wiki field as no wiki value.</summary>
    [Fact]
    public async Task ExplicitNullClearsKnownRawItemFactsAndPreservesConflicts()
    {
        var first = File();
        first.ClientSideApplicationId = "app";
        first.HtmlFileType = "html-type";
        first.FileType = "aspx";
        first.BSN = "raw-bsn";
        first.ContentTypeId = "0x0101";
        first.WikiFieldHasValue = true;
        first.ItemMetadataFieldStatesJson = "{\"ClientSideApplicationId\":\"value\",\"HTML_x0020_File_x0020_Type\":\"value\",\"File_x0020_Type\":\"value\",\"BSN\":\"value\",\"ContentTypeId\":\"value\",\"WikiField\":\"value\"}";
        var next = File();
        next.ItemMetadataFieldStatesJson = "{\"ClientSideApplicationId\":\"null\",\"HTML_x0020_File_x0020_Type\":\"null\",\"File_x0020_Type\":\"null\",\"BSN\":\"null\",\"ContentTypeId\":\"null\",\"WikiField\":\"null\"}";
        await Writer.WriteAsync(new[] { first, next });
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.ClientSideApplicationId.Should().BeNull();
        saved.HtmlFileType.Should().BeNull();
        saved.FileType.Should().BeNull();
        saved.BSN.Should().BeNull();
        saved.ContentTypeId.Should().BeNull();
        saved.WikiFieldHasValue.Should().BeFalse();
        saved.CoverageState.Should().Be("ChangedDuringScan");
        using var states = JsonDocument.Parse(saved.ItemMetadataFieldStatesJson);
        states.RootElement.EnumerateObject().Select(field => field.Value.GetString()).Should().OnlyContain(value => value == "null");
        saved.ProvenanceJson.Should().Contain("raw-bsn").And.Contain("html-type").And.Contain("0x0101").And.Contain("\"after\":null");
    }

    /// <summary>Does not trust a stale incoming raw value when the corresponding known field is explicitly marked as omitted.</summary>
    [Fact]
    public async Task ExplicitOmissionKeepsEarlierKnownFacts()
    {
        var first = File();
        first.ClientSideApplicationId = "earlier-app";
        first.WikiFieldHasValue = true;
        first.ItemMetadataFieldStatesJson = "{\"ClientSideApplicationId\":\"value\",\"WikiField\":\"value\"}";
        var omitted = File();
        omitted.ClientSideApplicationId = "must-not-replace";
        omitted.WikiFieldHasValue = false;
        omitted.ItemMetadataFieldStatesJson = "{\"ClientSideApplicationId\":\"omitted\",\"WikiField\":\"omitted\"}";
        await Writer.WriteAsync(new[] { first, omitted });
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.ClientSideApplicationId.Should().Be("earlier-app");
        saved.WikiFieldHasValue.Should().BeTrue();
        saved.CoverageState.Should().Be("Observed");
    }

    /// <summary>Normalizes an initial explicitly null field to its raw null/no-wiki fact rather than persisting contradictory caller leftovers.</summary>
    [Fact]
    public async Task InitialExplicitNullDoesNotPersistAContradictoryValue()
    {
        var first = File();
        first.BSN = "must-not-persist";
        first.WikiFieldHasValue = true;
        first.ItemMetadataFieldStatesJson = "{\"BSN\":\"null\",\"WikiField\":\"null\"}";
        await Writer.WriteAsync(new[] { first });
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.BSN.Should().BeNull();
        saved.WikiFieldHasValue.Should().BeFalse();
    }

    /// <summary>Rejects known fields marked as values when the corresponding raw fact is absent, before any valid batch member is committed.</summary>
    [Theory]
    [InlineData("ClientSideApplicationId")]
    [InlineData("HTML_x0020_File_x0020_Type")]
    [InlineData("File_x0020_Type")]
    [InlineData("BSN")]
    [InlineData("ContentTypeId")]
    [InlineData("WikiField")]
    [InlineData("NoExecute")]
    public async Task ReturnedValueStateRequiresTheKnownRawFact(string field)
    {
        var invalid = File(Guid.NewGuid());
        invalid.ItemMetadataFieldStatesJson = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = "value" });
        await FluentActions.Awaiting(() => Writer.WriteAsync(new[] { File(), invalid }))
            .Should().ThrowAsync<ArgumentException>();
        using var db = new ScanContext(options);
        (await db.SPFiles.CountAsync()).Should().Be(0);
    }

    /// <summary>Stores physical non-ASPX file facts without applying a business classification or extension admission rule in the writer.</summary>
    [Theory]
    [InlineData("asset.json")]
    [InlineData("theme.css")]
    [InlineData("image.png")]
    public async Task RawWriterDoesNotFilterPhysicalFilesByExtension(string name)
    {
        var observation = File();
        observation.FileName = name;
        observation.ServerRelativeUrl = "/sites/test/" + name;
        await Writer.WriteAsync(new[] { observation });
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.FileName.Should().Be(name);
        saved.ServerRelativeUrl.Should().Be(observation.ServerRelativeUrl);
    }

    /// <summary>Clears a returned NoExecute null without converting it to zero and preserves the exact prior raw text and read outcome as conflict evidence.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public async Task ExplicitNoExecuteNullClearsRawAndRetainsBeforeAfter(string raw)
    {
        var first = File();
        first.NoExecuteRaw = raw;
        first.NoExecuteSource = "SPO.ListItem.NoExecute";
        first.NoExecuteReadState = "Available";
        first.ItemMetadataFieldStatesJson = "{\"NoExecute\":\"value\"}";
        await Writer.WriteAsync(new[] { first });
        var next = File();
        next.NoExecuteSource = "SPO.ListItem.NoExecute";
        next.NoExecuteReadState = "Null";
        next.ItemMetadataFieldStatesJson = "{\"NoExecute\":\"null\"}";
        await Writer.WriteAsync(new[] { next });
        await Writer.WriteAsync(new[] { next });
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.NoExecuteRaw.Should().BeNull();
        saved.NoExecuteSource.Should().Be("SPO.ListItem.NoExecute");
        saved.NoExecuteReadState.Should().Be("Null");
        saved.ItemMetadataFieldStatesJson.Should().Be("{\"NoExecute\":\"null\"}");
        saved.CoverageState.Should().Be("ChangedDuringScan");
        using var evidence = JsonDocument.Parse(saved.ProvenanceJson);
        var rawConflict = evidence.RootElement.EnumerateArray().Single(entry =>
            entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("field", out var field) &&
            field.GetString() == nameof(SPFile.NoExecuteRaw));
        rawConflict.GetProperty("before").GetString().Should().Be(raw);
        rawConflict.GetProperty("after").ValueKind.Should().Be(JsonValueKind.Null);
        var stateConflict = evidence.RootElement.EnumerateArray().Single(entry =>
            entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("field", out var field) &&
            field.GetString() == nameof(SPFile.NoExecuteReadState));
        stateConflict.GetProperty("before").GetString().Should().Be("Available");
        stateConflict.GetProperty("after").GetString().Should().Be("Null");
    }

    /// <summary>Keeps an initially returned NoExecute null, its observed source, and its Null read label without manufacturing a numeric value.</summary>
    [Fact]
    public async Task InitialNoExecuteNullRemainsAnExplicitRawNull()
    {
        var first = File();
        first.NoExecuteRaw = "must-not-persist";
        first.NoExecuteSource = "SPO.ListItem.NoExecute";
        first.NoExecuteReadState = "Null";
        first.ItemMetadataFieldStatesJson = "{\"NoExecute\":\"null\"}";
        await Writer.WriteAsync(new[] { first });
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.NoExecuteRaw.Should().BeNull();
        saved.NoExecuteSource.Should().Be("SPO.ListItem.NoExecute");
        saved.NoExecuteReadState.Should().Be("Null");
        saved.CoverageState.Should().Be("Observed");
    }

    /// <summary>Preserves both known NoExecute values and known nulls, including their source, when a later read omits the field or fails.</summary>
    [Theory]
    [InlineData(false, "NotRequested", "Observed")]
    [InlineData(true, "NotRequested", "Observed")]
    [InlineData(false, "Unavailable", "ChangedDuringScan")]
    [InlineData(true, "Unavailable", "ChangedDuringScan")]
    public async Task OmittedNoExecuteRetainsEarlierRawFact(bool explicitNull, string readState, string coverage)
    {
        var first = File();
        first.NoExecuteRaw = explicitNull ? null : "1";
        first.NoExecuteSource = "SPO.ListItem.NoExecute";
        first.NoExecuteReadState = explicitNull ? "Null" : "Available";
        first.ItemMetadataFieldStatesJson = explicitNull ? "{\"NoExecute\":\"null\"}" : "{\"NoExecute\":\"value\"}";
        await Writer.WriteAsync(new[] { first });
        var omitted = File();
        omitted.NoExecuteRaw = "must-not-replace";
        omitted.NoExecuteSource = "must-not-replace";
        omitted.NoExecuteReadState = readState;
        omitted.ItemMetadataFieldStatesJson = "{\"NoExecute\":\"omitted\"}";
        await Writer.WriteAsync(new[] { omitted });
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.NoExecuteRaw.Should().Be(explicitNull ? null : "1");
        saved.NoExecuteSource.Should().Be("SPO.ListItem.NoExecute");
        saved.NoExecuteReadState.Should().Be(explicitNull ? "Null" : "Available");
        saved.ItemMetadataFieldStatesJson.Should().Be(first.ItemMetadataFieldStatesJson);
        saved.CoverageState.Should().Be(coverage);
    }

    /// <summary>Allows a returned raw value to supersede an explicit null and records that transition without parsing or expanding its zero text.</summary>
    [Fact]
    public async Task ReturnedNoExecuteValueSupersedesNullWithRawConflictEvidence()
    {
        var first = File();
        first.NoExecuteSource = "SPO.ListItem.NoExecute";
        first.NoExecuteReadState = "Null";
        first.ItemMetadataFieldStatesJson = "{\"NoExecute\":\"null\"}";
        await Writer.WriteAsync(new[] { first });
        var next = File();
        next.NoExecuteRaw = "0";
        next.NoExecuteSource = "SPO.ListItem.NoExecute";
        next.NoExecuteReadState = "Available";
        next.ItemMetadataFieldStatesJson = "{\"NoExecute\":\"value\"}";
        await Writer.WriteAsync(new[] { next });
        using var db = new ScanContext(options);
        var saved = await db.SPFiles.SingleAsync();
        saved.NoExecuteRaw.Should().Be("0");
        saved.NoExecuteReadState.Should().Be("Available");
        saved.ItemMetadataFieldStatesJson.Should().Be("{\"NoExecute\":\"value\"}");
        using var evidence = JsonDocument.Parse(saved.ProvenanceJson);
        var rawConflict = evidence.RootElement.EnumerateArray().Single(entry =>
            entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("field", out var field) &&
            field.GetString() == nameof(SPFile.NoExecuteRaw));
        rawConflict.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Null);
        rawConflict.GetProperty("after").GetString().Should().Be("0");
    }
}
