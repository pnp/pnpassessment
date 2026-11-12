#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PnP.Scanning.Core.ClassicPages.Analysis.Module;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.List;
using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Contracts.Site;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>Locks deterministic v4 replay and unchanged ASPX/Posts publication and evidence merging against the v3 baseline.</summary>
public sealed class ClassicPagesRefactorEquivalenceTests
{
    private const string Site = "https://contoso.sharepoint.com/sites/golden";
    private static readonly Guid Assessment = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Snapshot = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid SiteId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid WebId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid LibraryId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid PostsId = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly DateTime Observed = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Replays fixed bytes offline and preserves native identities, mapping arithmetic and the exact existing report envelope.</summary>
    [Fact]
    public async Task V4_replay_retains_unchanged_aspx_posts_publication_bytes()
    {
        MemorySnapshot snapshot = Fixture();
        string sourceHash = SourceHash(snapshot.Records);
        ClassicPageAnalysisModule module = new();
        await module.PrepareAsync(snapshot, default);
        foreach (SourceRecord source in snapshot.Records)
            snapshot.Results.Add(await module.AnalyzeAsync(source, VersionedJson.Empty, default));
        await module.FinalizeAsync(snapshot, snapshot, snapshot, VersionedJson.Empty, default);

        Assert.Equal(sourceHash, SourceHash(snapshot.Records));
        AnalysisReportRow publication = Assert.Single(snapshot.Reports);
        Assert.Equal("ClassicCompatibility", publication.Kind);
        Assert.Equal("assessment", publication.Key);
        Assert.Equal(0, publication.Ordinal);
        string resultHash = Hash(Encoding.UTF8.GetBytes(VersionedJson.From(snapshot.Results).Json));
        string publicationHash = Hash(Encoding.UTF8.GetBytes(publication.Payload.Json));
        Assert.True(
            sourceHash == "2455118e03ad4e87f24984830935dadc0aeed4ede95ae590b59f99b1b9b4d1d9" &&
            resultHash == "e2674cbda8489f6ccb6a569f9880714c779c6c837e00af79ced0a279ff945572" &&
            publicationHash == "d24b6ac3a8e5900d23609829af1445446518565afff9ab2635cf6190aa48f077",
            $"Source={sourceHash}; Results={resultHash}; Publication={publicationHash}");
        JsonElement[] pages = publication.Payload.Value.GetProperty("Pages").EnumerateArray().ToArray();
        Assert.Equal(4, pages.Length);
        Assert.Equal(2, pages.Count(page => page.GetProperty("SourceKind").GetString() == "ListItem"));
        Assert.Equal(
            38,
            pages.Single(page => page.GetProperty("PageName").GetString() == "Golden page")
                .GetProperty("MappingPercentage")
                .GetDouble());
    }

    /// <summary>Preserves null, omitted, failed, conflicting, multiline and time evidence through one pure consolidation algorithm.</summary>
    [Fact]
    public void Physical_evidence_merge_keeps_the_baseline_bytes_and_does_not_mutate_inputs()
    {
        PhysicalFileFacts first = File(1, "page.aspx", 1);
        first.NoExecuteRaw = " 1 ";
        first.NoExecuteSource = "SPO.ListItem.NoExecute";
        first.NoExecuteReadState = "Available";
        first.WikiFieldHasValue = true;
        first.ItemMetadataFieldStatesJson = "{\"NoExecute\":\"value\",\"WikiField\":\"value\"}";
        first.ProvenanceJson = "[{\"z\":2,\"a\":{\"second\":2,\"first\":1}}]";
        PhysicalFileFacts second = CollectionJson.Convert<PhysicalFileFacts>(first);
        second.ObservedAtUtc = Observed.AddMinutes(2);
        second.FirstObservedAtUtc = default;
        second.ServerRelativeUrl = "/sites/golden/Pages/renamed.aspx";
        second.NoExecuteRaw = null;
        second.NoExecuteReadState = "Null";
        second.WikiFieldHasValue = false;
        second.ItemMetadataFieldStatesJson = "{\"NoExecute\":\"null\",\"WikiField\":\"null\"}";
        second.ProvenanceJson = "[{\"source\":\"metadata\"}]";
        second.FileErrorDetail = "403 while reading\noriginal multiline evidence";
        PhysicalFileFacts omitted = CollectionJson.Convert<PhysicalFileFacts>(second);
        omitted.ItemMetadataFieldStatesJson = "{\"NoExecute\":\"omitted\",\"WikiField\":\"omitted\"}";
        omitted.NoExecuteReadState = "NotReturned";
        omitted.ObservedAtUtc = Observed.AddMinutes(3);
        string original = JsonSerializer.Serialize(new[] { first, second, omitted });

        PhysicalFileFacts merged = PhysicalFileConsolidator.Consolidate(null, first);
        merged = PhysicalFileConsolidator.Consolidate(merged, second);
        merged = PhysicalFileConsolidator.Consolidate(merged, omitted);
        string mergedJson = JsonSerializer.Serialize(merged);
        Assert.Equal(original, JsonSerializer.Serialize(new[] { first, second, omitted }));
        Assert.Equal(mergedJson, JsonSerializer.Serialize(PhysicalFileConsolidator.Consolidate(merged, omitted)));
        Assert.Equal("Null", merged.NoExecuteReadState);
        Assert.Null(merged.NoExecuteRaw);
        Assert.False(merged.WikiFieldHasValue);
        Assert.Equal(Observed, merged.FirstObservedAtUtc);
        Assert.Equal(Observed.AddMinutes(3), merged.ObservedAtUtc);
        Assert.Equal("ChangedDuringScan", merged.CoverageState);
        string mergedHash = Hash(Encoding.UTF8.GetBytes(mergedJson));
        Assert.Equal("bff7e5f0946e3001c57a3ae90148113682cfb91b8212d6f6f571f7d68255a09f", mergedHash);
    }

    /// <summary>Retains the legacy distinction between missing, explicitly null and empty BSN fields.</summary>
    [Theory]
    [InlineData("missing", ClassicPageRules.ASPXPage)]
    [InlineData("null", ClassicPageRules.ASPXPage)]
    [InlineData("empty", ClassicPageRules.WikiPage)]
    [InlineData("number", ClassicPageRules.ASPXPage)]
    public void Legacy_item_field_classification_keeps_its_null_and_scalar_rules(string state, string expected)
    {
        Dictionary<string, object?> fields = [];
        if (state == "null") fields["BSN"] = null;
        if (state == "empty") fields["BSN"] = "";
        if (state == "number") fields["BSN"] = 265;
        Assert.Equal(expected, ClassicPageRules.GetPageType(fields));
    }

    /// <summary>Constructs fixed synthetic receipts including a file-only object, shared renderer items and eight ordered parts.</summary>
    private static MemorySnapshot Fixture()
    {
        ClassicCollectionOptions options = new(false, true, true, false, 14);
        WebFacts web = new(
            Site,
            "/",
            "STS#0",
            SiteId,
            WebId,
            Observed,
            [],
            [],
            "Pages/page.aspx",
            SourceReadState.Complete,
            true,
            SourceReadState.Complete,
            null,
            0,
            null,
            SourceReadState.NotAttempted,
            SourceReadState.Complete);
        SiteCollectionScope siteScope = new(Site, [new(Site, "/", "STS#0")], SourceReadState.Complete);
        DiscoveryEvidence selection = new()
        {
            ScanId = Assessment,
            RecordKey = "assessment:site-selection",
            RowType = DiscoveryRowType.Scope,
            ScopeType = "SiteSelection",
            Url = Site,
            DiscoveryStatus = "Complete",
            ObservationMethod = "DeclaredSites",
            ExpectedChildCount = 1,
            ObservedChildCount = 1,
            ObservedAtUtc = Observed,
        };
        ClassicCollectionScope scope = new(
            [Site],
            [siteScope],
            true,
            options,
            Observed.AddDays(-14),
            Observed,
            [selection]);
        PhysicalFileFacts file = File(1, "page.aspx", 1);
        DiscoveryEvidence candidate = new()
        {
            ScanId = Assessment,
            SiteUrl = Site,
            WebUrl = "/",
            RecordKey = "page:golden",
            RowType = DiscoveryRowType.Page,
            ScopeType = "File",
            Url = file.ServerRelativeUrl,
            FileName = file.FileName,
            SiteCollectionId = SiteId,
            WebId = WebId,
            ListId = LibraryId,
            ListItemId = 1,
            FileUniqueId = file.FileUniqueId,
            DiscoveryStatus = "Discovered",
            CustomizedPageStatus = "Uncustomized",
            ObservedAtUtc = Observed,
        };
        ClassicPageWebPartSource[] parts = Enumerable.Range(0, 8)
            .Select(ordinal =>
            {
                string type = ordinal < 3
                    ? "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart, Microsoft.SharePoint, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c"
                    : "Contoso.Widget, Contoso";
                return new ClassicPageWebPartSource(
                    new Guid(ordinal + 1, 0, 0, new byte[8]),
                    "part-" + ordinal,
                    "Main",
                    ordinal,
                    "Part " + ordinal,
                    false,
                    false,
                    "All",
                    [],
                    $"<webParts xmlns=\"http://schemas.microsoft.com/WebPart/v3\"><webPart><metaData><type name=\"{type}\" /></metaData></webPart></webParts>",
                    SourceReadState.Complete);
            })
            .ToArray();
        FilePageEvidence enrichment = new(
            Site,
            "/",
            candidate.RecordKey,
            LibraryId,
            "Pages",
            "/sites/golden/Pages",
            new()
            {
                ["ID"] = SourceField.Of("Int32", 1),
                ["FileRef"] = SourceField.Of("String", file.ServerRelativeUrl),
                ["Title"] = SourceField.Of("String", "Golden page"),
                ["HTML_x0020_File_x0020_Type"] = SourceField.Of("String", "SharePoint.WebPartPage.Document"),
            },
            [],
            parts,
            SourceReadState.Complete,
            SourceReadState.Complete,
            null,
            SourceReadState.NotAttempted);
        ListItemQueryScope query = new(
            PostsId,
            "Posts",
            "/sites/golden/Lists/Posts",
            ListItemQueryKind.Posts,
            301,
            "/sites/golden/Lists/Posts/Post.aspx");
        ListItemBatch items = new(
            Site,
            "/",
            PostsId,
            query.ListTitle,
            query.ListUrl,
            [Item(1), Item(2)],
            null,
            SourceReadState.Complete,
            ListItemQueryKind.Posts,
            301,
            query.DisplayFormUrl);
        return new(
        [
            Record(1, "CollectionScope", scope), Record(2, "WebFacts", web),
            Record(
                3,
                "FileInventory",
                new WebFileInventory(Site, "/", [candidate], [file, File(2, "inventory-only.aspx", null)])),
            Record(4, "FileEnrichment", enrichment),
            Record(5, "ListItemQueries", new WebListItemQueries(Site, "/", [query], SourceReadState.Complete)),
            Record(6, "ListItems", items),
        ]);
    }

    /// <summary>Creates an independent Posts item whose shared renderer is a declaration, not physical identity.</summary>
    private static ListItemFacts Item(int id) =>
        new(id, new() { ["ID"] = SourceField.Of("Int32", id), ["Title"] = SourceField.Of("String", "Post " + id), });

    /// <summary>Creates stable physical facts with exact original ASPX bytes and explicit role evidence.</summary>
    private static PhysicalFileFacts File(int id, string name, int? item) =>
        new()
        {
            ScanId = Assessment,
            SiteCollectionId = SiteId,
            WebId = WebId,
            FileUniqueId = new Guid(id, 0, 0, new byte[8]),
            ListId = item.HasValue ? LibraryId : null,
            ListItemId = item,
            SiteUrl = Site,
            WebUrl = "/",
            FileName = name,
            ServerRelativeUrl = "/sites/golden/Pages/" + name,
            FileReadState = "Complete",
            ItemMetadataReadState = "NotRequested",
            NoExecuteReadState = "NotRequested",
            CoverageState = "Observed",
            FirstObservedAtUtc = Observed,
            ObservedAtUtc = Observed,
            AspxEvidenceJson = JsonSerializer.Serialize(
                new AspxPageEvidence(
                    "Available",
                    null,
                    null,
                    "DefaultAssumed",
                    null,
                    null,
                    null,
                    false,
                    false,
                    false,
                    "Complete",
                    Observed,
                    OriginalBytes: [60, 37, 64, 32, 80, 97, 103, 101, 32, 37, 62])),
        };

    /// <summary>Wraps a source in the unchanged v3 envelope with a stable observation ID and original byte digest.</summary>
    private static SourceRecord Record<T>(int ordinal, string kind, T input)
    {
        byte[] bytes = CollectionJson.Bytes(input);
        return new(
            new Guid(ordinal, 0, 0, new byte[8]),
            Snapshot,
            kind,
            null,
            AcquisitionStatus.Complete,
            VersionedJson.From(new { kind }),
            new(bytes.Length, Hash(bytes), bytes),
            null);
    }

    /// <summary>Hashes the exact captured source envelopes in declared receipt order.</summary>
    private static string SourceHash(IEnumerable<SourceRecord> records) =>
        Hash(
            Encoding.UTF8.GetBytes(
                string.Join('\n', records.Select(record => Encoding.UTF8.GetString(record.Artifact.GetBytes()!)))));

    /// <summary>Produces the existing lowercase SHA-256 representation.</summary>
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Provides only detached snapshot, committed-result and publication capabilities; no network services exist.</summary>
    private sealed class MemorySnapshot(SourceRecord[] records)
        : ISnapshotReader, IAnalysisResultReader, IAnalysisReportWriter
    {
        internal SourceRecord[] Records { get; } = records;
        internal List<AnalysisResult> Results { get; } = [];
        internal IReadOnlyList<AnalysisReportRow> Reports { get; private set; } = [];

        /// <summary>Opens the fixed v3 manifest without acquiring or rewriting source data.</summary>
        public Task<SnapshotInfo> OpenAsync(CancellationToken cancellationToken) =>
            Task.FromResult(
                new SnapshotInfo(
                    Assessment,
                    Snapshot,
                    ClassicContentContract.ModuleKey,
                    ClassicContentContract.InputVersion,
                    VersionedJson.Empty,
                    "fixed-manifest",
                    Records.Select(record => record.ObservationId)));

        /// <summary>Returns the exact previously captured observation.</summary>
        public Task<SourceRecord> ReadAsync(Guid observationId, CancellationToken cancellationToken) =>
            Task.FromResult(Records.Single(record => record.ObservationId == observationId));

        /// <summary>Returns deterministic committed results in original source order.</summary>
        public Task<IReadOnlyList<AnalysisResult>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AnalysisResult>>(Results);

        /// <summary>Captures the immutable report rows for byte-level comparison.</summary>
        public Task PublishAsync(IReadOnlyList<AnalysisReportRow> rows, CancellationToken cancellationToken)
        {
            Reports = rows;
            return Task.CompletedTask;
        }
    }
}