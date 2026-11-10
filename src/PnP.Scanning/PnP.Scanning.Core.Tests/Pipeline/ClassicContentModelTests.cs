using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Compatibility;
using PnP.Scanning.Core.ClassicPages;
#nullable enable
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Collection.List;
using PnP.Scanning.Core.ClassicPages.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>
/// Verifies acquisition-object boundaries, native Page identities, immutable replay, and explicit legacy compatibility limitations.
/// </summary>
[Trait("Category", "Pipeline")]
public sealed class ClassicContentModelTests
{
    /// <summary>
    /// Verifies ASPX and Posts retain independent native identities while retired non-ASPX files remain raw evidence only.
    /// </summary>
    [Fact]
    public async Task Aspx_and_posts_publish_native_pages_without_promoting_non_aspx_inventory()
    {
        using StoreCase data = new("classic-native-content");
        ClassicContentFixture source = new()
        {
            IncludeBlog = true, IncludeSecondBlog = true, IncludeRetiredFile = true
        };
        Guid rendererFileId = Guid.NewGuid();
        source.TransformInventory = inventory => inventory with
        {
            Files =
            [
                .. inventory.Files!,
                new PhysicalFileFacts
                {
                    ScanId = inventory.Files![0].ScanId,
                    SiteCollectionId = ClassicContentFixture.SiteCollectionId,
                    WebId = ClassicContentFixture.WebId,
                    ListId = ClassicContentFixture.PostsListId,
                    FileUniqueId = rendererFileId,
                    FileName = "Post.aspx",
                    SiteUrl = ClassicContentFixture.Site,
                    WebUrl = "/",
                    ServerRelativeUrl = "/sites/a/Lists/Posts/Post.aspx",
                    FileReadState = "Complete",
                    ItemMetadataReadState = "NotRequested",
                    NoExecuteReadState = "NotRequested",
                    CoverageState = "Observed",
                }
            ],
        };
        TestExecution execution = await StartAsync(data, source);
        await AssertStatusAsync(data, execution, ScanStatus.Finished);
        ClassicCompatibilityPayload publication = await PublicationAsync(data, execution.AssessmentId);
        Assert.Contains(publication.Files, file => file.FileUniqueId == ClassicContentFixture.RetiredFileId);
        Assert.DoesNotContain(publication.Pages, page => page.FileUniqueId == ClassicContentFixture.RetiredFileId);
        ClassicPageRow[] posts = publication.Pages.Where(page => page.Identity!.ObjectKind == PageObjectKind.ListItem)
            .ToArray();
        Assert.Equal(4, posts.Length);
        Assert.Equal(2, posts.Select(page => page.ListId).Distinct().Count());
        Assert.All(posts, page => Assert.Null(page.FileUniqueId));
        Assert.Equal(4, posts.Select(page => page.Identity).Distinct().Count());
        Assert.Contains(publication.Files, file => file.FileUniqueId == rendererFileId);
        Assert.Contains(
            publication.Pages,
            page => page.Identity == PageIdentity.File(ClassicContentFixture.SiteCollectionId, rendererFileId));
        Assert.Equal(2, posts.Count(page => page.PageUrl!.StartsWith("/sites/a/Lists/Posts/Post.aspx?ID=")));
        ClassicPageRow orphan = Assert.Single(
            publication.Pages,
            page => page.FileUniqueId == Guid.Parse("00000000-0000-0000-0000-000000000006"));
        Assert.Equal(PageObjectKind.File, orphan.Identity!.ObjectKind);
        Assert.Null(orphan.ListId);
        Assert.Null(orphan.ListItemId);
        using ScanContext db = data.Store.CreateContext(execution.AssessmentId);
        Assert.DoesNotContain(
            await db.ClassicPages.ToListAsync(),
            page => page.PageType == ClassicPageRules.ModernPage);
        Assert.Contains(publication.Pages, page => page.PageType == ClassicPageRules.ModernPage);
        ListItemBatch[] batches = (await db.SourceObservations.ToListAsync())
            .Where(row => CollectionJson.Kind(ToRecord(row)) == "ListItems")
            .Select(row => CollectionJson.Read<ListItemBatch>(row.RawBytes))
            .ToArray();
        Assert.All(batches, batch => Assert.Equal(301, batch.ListTemplate));
        Assert.Equal(2, batches.Select(batch => batch.ListId).Distinct().Count());
    }

    /// <summary>
    /// Verifies native item replay is idempotent while distinct native items sharing a renderer remain separate Pages.
    /// </summary>
    [Fact]
    public async Task Repeated_identical_native_items_are_idempotent_and_shared_renderers_are_not_identity()
    {
        using StoreCase data = new("classic-identical-items");
        ClassicContentFixture source = new() { IncludeBlog = true, IncludeSecondBlog = true };
        source.TransformBatch = batch => batch with
        {
            Items = batch.Items.Select(item => item with
                {
                    ListItemId = 100,
                    Fields = new Dictionary<string, SourceField>(item.Fields)
                    {
                        ["ID"] = SourceField.Of("Int32", 100)
                    },
                })
                .ToArray(),
        };
        TestExecution execution = await StartAsync(data, source);
        await AssertStatusAsync(data, execution, ScanStatus.Finished);
        ClassicCompatibilityPayload publication = await PublicationAsync(data, execution.AssessmentId);
        ClassicPageRow[] posts = publication.Pages.Where(page => page.Identity!.ObjectKind == PageObjectKind.ListItem)
            .ToArray();
        Assert.Equal(2, posts.Length);
        Assert.All(posts, page => Assert.Equal(100, page.ListItemId));
        Assert.NotEqual(posts[0].Identity, posts[1].Identity);
    }

    /// <summary>
    /// Verifies contradictory repeat observations fail sealing rather than silently selecting the last item.
    /// </summary>
    [Fact]
    public async Task Conflicting_fields_for_the_same_native_item_prevent_snapshot_sealing()
    {
        using StoreCase data = new("classic-conflicting-items");
        ClassicContentFixture source = new() { IncludeBlog = true };
        int ordinal = 0;
        source.TransformBatch = batch => batch with
        {
            Items = batch.Items.Select(item => item with
                {
                    ListItemId = 100,
                    Fields = new Dictionary<string, SourceField>(item.Fields)
                    {
                        ["ID"] = SourceField.Of("Int32", 100),
                        ["Title"] = SourceField.Of("String", "Observation " + ordinal++),
                    },
                })
                .ToArray(),
        };
        TestExecution execution = await StartAsync(data, source);
        await AssertStatusAsync(data, execution, ScanStatus.Terminated);
        using ScanContext db = data.Store.CreateContext(execution.AssessmentId);
        Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
        Assert.Empty(await db.AnalysisRuns.ToListAsync());
        Assert.Contains("conflicting fields", (await data.Store.LatestRootAsync(execution.AssessmentId))!.LastError);
    }

    /// <summary>
    /// Verifies repeated discovery, pagination, missing GUIDs, and contradictory native backing-item relationships cannot seal.
    /// </summary>
    [Theory]
    [InlineData("candidate")]
    [InlineData("paging")]
    [InlineData("file-guid")]
    [InlineData("backing-item")]
    public async Task Conflicting_native_acquisition_cannot_be_published_as_success(string conflict)
    {
        using StoreCase data = new("classic-native-conflict");
        ClassicContentFixture source = new() { IncludeBlog = true, IncludeRetiredFile = true };
        if (conflict == "candidate")
            source.TransformInventory = inventory =>
                inventory with { Evidence = [.. inventory.Evidence, inventory.Evidence[0]] };
        else if (conflict == "file-guid")
            source.TransformInventory = inventory =>
            {
                inventory.Files![0].FileUniqueId = Guid.Empty;
                return inventory;
            };
        else if (conflict == "paging")
            source.TransformBatch = batch => batch with { NextPage = "repeated-native-token" };
        else if (conflict == "backing-item")
            source.TransformInventory = inventory =>
            {
                PhysicalFileFacts conflictFile = CollectionJson.Convert<PhysicalFileFacts>(inventory.Files![0]);
                conflictFile.ListItemId = 999;
                return inventory with { Files = [.. inventory.Files, conflictFile] };
            };
        else
            throw new ArgumentOutOfRangeException(nameof(conflict));
        TestExecution execution = await StartAsync(data, source);
        await AssertStatusAsync(data, execution, ScanStatus.Terminated);
        using ScanContext db = data.Store.CreateContext(execution.AssessmentId);
        Assert.False((await db.SourceSnapshots.SingleAsync()).IsSealed);
        Assert.Empty(await db.AnalysisReportRows.ToListAsync());
        Assert.Empty(await db.AnalysisRuns.ToListAsync());
        Assert.NotEmpty((await data.Store.LatestRootAsync(execution.AssessmentId))!.LastError!);
    }

    /// <summary>Reads physical metadata by exact item ID and scans only Posts content, never a document-library extension query.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Query_builders_separate_exact_file_metadata_from_posts_content(bool skipUsers)
    {
        string physical = ListItemQueryBuilder.ForFileItem(7, skipUsers);
        string posts = ListItemQueryBuilder.ForPosts(skipUsers);
        Assert.Contains("<Value Type='Counter'>7</Value>", physical);
        Assert.Contains("<RowLimit Paged='TRUE'>2</RowLimit>", physical);
        Assert.Contains("NoExecute", physical);
        Assert.DoesNotContain("<Where>", posts);
        Assert.DoesNotContain("FileRef", posts);
        Assert.DoesNotContain("File_x0020_Type", posts);
        Assert.DoesNotContain("pointpub", posts);
        Assert.Equal(!skipUsers, posts.Contains("Editor", StringComparison.Ordinal));
        Assert.Contains("<RowLimit Paged='TRUE'>1000</RowLimit>", posts);
    }

    /// <summary>Does not admit ordinary lists or document libraries as independent content, irrespective of form declarations.</summary>
    [Theory]
    [InlineData(100, false)]
    [InlineData(101, false)]
    [InlineData(119, false)]
    [InlineData(301, true)]
    [InlineData(850, false)]
    public void Only_the_real_posts_template_selects_independent_items(int template, bool expected) =>
        Assert.Equal(expected, ListItemContentReader.IsPostsTemplate(template));

    /// <summary>Retains failed and unattempted Posts reads as unknown content and incomplete coverage, never authoritative zero.</summary>
    [Theory]
    [InlineData("Denied", "Incomplete")]
    [InlineData("Failed", "Incomplete")]
    [InlineData("NotAttempted", "Unknown")]
    public async Task Unavailable_posts_acquisition_never_becomes_complete_empty_content(string status, string coverage)
    {
        using StoreCase data = new("classic-posts-unavailable");
        ClassicContentFixture source = new() { IncludeBlog = true };
        source.TransformBatch = batch => batch with
        {
            Items = [], NextPage = null, State = new(status, "Synthetic Posts read was not completed"),
        };
        TestExecution execution = await StartAsync(data, source);
        await AssertStatusAsync(data, execution, ScanStatus.Finished);
        ClassicCompatibilityPayload publication = await PublicationAsync(data, execution.AssessmentId);
        Assert.DoesNotContain(publication.Pages, page => page.Identity!.ObjectKind == PageObjectKind.ListItem);
        Assert.Contains(
            publication.Discovery,
            row => row.ErrorStage == "ListItemAcquisition" && row.AssessmentStatus == "Unknown" &&
                   row.ErrorDetail == "Synthetic Posts read was not completed");
        Assert.Equal(
            coverage,
            publication.Discovery.Single(row => row.RecordKey == "summary:coverage").DiscoveryStatus);
        using ScanContext db = data.Store.CreateContext(execution.AssessmentId);
        AnalysisResultRow result = (await db.AnalysisResults.ToListAsync()).Single(row =>
            new VersionedJson(row.PayloadJson).Value.GetProperty("kind").GetString() == "ListItems");
        Assert.Equal(AnalysisOutcome.Unknown, result.Outcome);
    }

    /// <summary>
    /// Verifies identical original bytes and source hashes cannot merge different physical GUIDs.
    /// </summary>
    [Fact]
    public async Task Equal_content_hashes_do_not_merge_distinct_file_guids()
    {
        using StoreCase data = new("classic-same-bytes");
        ClassicContentFixture source = new();
        byte[] bytes = Encoding.UTF8.GetBytes("<%@ Page Inherits=\"System.Web.UI.Page\" %>");
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        source.TransformInventory = inventory => inventory with
        {
            Files = inventory.Files!.Concat(
                    new[] { firstId, secondId }.Select(fileId => new PhysicalFileFacts
                    {
                        ScanId = inventory.Files![0].ScanId,
                        SiteCollectionId = ClassicContentFixture.SiteCollectionId,
                        WebId = ClassicContentFixture.WebId,
                        FileUniqueId = fileId,
                        SiteUrl = ClassicContentFixture.Site,
                        WebUrl = "/",
                        FileName = fileId + ".aspx",
                        ServerRelativeUrl = "/sites/a/" + fileId + ".aspx",
                        FileReadState = "Complete",
                        CoverageState = "Observed",
                        ItemMetadataReadState = "NotRequested",
                        NoExecuteReadState = "NotRequested",
                        AspxEvidenceJson = JsonSerializer.Serialize(
                            new AspxPageEvidence(
                                "Available",
                                null,
                                null,
                                "PendingAnalysis",
                                null,
                                null,
                                null,
                                false,
                                false,
                                false,
                                "Complete",
                                DateTime.UnixEpoch,
                                bytes)),
                    }))
                .ToArray(),
        };
        TestExecution execution = await StartAsync(data, source);
        await AssertStatusAsync(data, execution, ScanStatus.Finished);
        ClassicCompatibilityPayload publication = await PublicationAsync(data, execution.AssessmentId);
        ClassicPageRow[] pages = publication.Pages
            .Where(page => page.FileUniqueId == firstId || page.FileUniqueId == secondId)
            .ToArray();
        Assert.Equal(2, pages.Length);
        Assert.NotEqual(pages[0].Identity, pages[1].Identity);
        Assert.Equal(
            PhysicalFileAnalysisRules.Analyze(
                    CollectionJson.Convert<PhysicalFileFacts>(
                        publication.Files.Single(file => file.FileUniqueId == firstId)))
                .SourceSha256,
            PhysicalFileAnalysisRules.Analyze(
                    CollectionJson.Convert<PhysicalFileFacts>(
                        publication.Files.Single(file => file.FileUniqueId == secondId)))
                .SourceSha256);
    }

    /// <summary>
    /// Verifies the URL-keyed legacy adapter reports collisions instead of silently merging distinct native item Pages.
    /// </summary>
    [Fact]
    public async Task Legacy_url_collisions_preserve_native_publication_and_fail_materialization_atomically()
    {
        using StoreCase data = new("classic-legacy-key-collision");
        ClassicContentFixture source = new() { IncludeBlog = true, IncludeSecondBlog = true };
        source.TransformQueries = scope => scope with
        {
            Queries = scope.Queries.Select(query => query with { DisplayFormUrl = "/sites/a/Shared/Post.aspx" })
                .ToArray(),
        };
        TestExecution execution = await StartAsync(data, source);
        await AssertStatusAsync(data, execution, ScanStatus.Terminated);
        ClassicCompatibilityPayload publication = await PublicationAsync(data, execution.AssessmentId);
        ClassicPageRow first = publication.Pages.First(page => page.Identity!.ObjectKind == PageObjectKind.ListItem);
        ClassicPageRow second = publication.Pages.First(page =>
            page.Identity!.ObjectKind == PageObjectKind.ListItem && page.ListId != first.ListId);
        Assert.Equal(first.PageUrl, second.PageUrl);
        SnapshotIntegrityException collision =
            Assert.Throws<SnapshotIntegrityException>(() => ClassicCompatibility.LegacyPages([first, second]));
        Assert.Contains("Distinct native content", collision.Message);
        Assert.NotEqual(first.Identity, second.Identity);
        ClassicCompatibilityPayload unchanged = await PublicationAsync(data, execution.AssessmentId);
        Assert.Equal(4, unchanged.Pages.Count(page => page.Identity!.ObjectKind == PageObjectKind.ListItem));
        using ScanContext db = data.Store.CreateContext(execution.AssessmentId);
        Assert.Empty(await db.ClassicPages.ToListAsync());
        Assert.True((await db.SourceSnapshots.SingleAsync()).IsSealed);
        Assert.Equal(
            ScanStatus.Finished,
            (await db.PhaseRuns.SingleAsync(phase => phase.Kind == PhaseKind.Analysis)).Status);
    }

    /// <summary>
    /// Verifies home-only selection filters Page publication without discarding the underlying physical inventory.
    /// </summary>
    [Fact]
    public async Task Home_only_mode_keeps_the_inventory_but_only_publishes_the_selected_home_page()
    {
        using StoreCase data = new("classic-home-only-native");
        ClassicContentFixture source = new() { IncludeBlog = true, IncludeRetiredFile = true };
        TestExecution execution = await StartAsync(data, source, homeOnly: true);
        await AssertStatusAsync(data, execution, ScanStatus.Finished);
        ClassicCompatibilityPayload publication = await PublicationAsync(data, execution.AssessmentId);
        Assert.Single(publication.Pages);
        Assert.True(publication.Pages[0].HomePage);
        Assert.Equal(9, publication.Files.Length);
        Assert.DoesNotContain("blog-lists", source.Calls.Keys);
    }

    /// <summary>
    /// Verifies unsupported sealed legacy source shapes are rejected before creating analysis or changing immutable bytes.
    /// </summary>
    [Theory]
    [InlineData("classicpage-source-v1")]
    [InlineData("classicpage-source-v2")]
    [InlineData("classic-objects-v3")]
    public async Task Sealed_legacy_snapshots_are_rejected_without_reinterpretation_or_mutation(string inputVersion)
    {
        using StoreCase data = new("classic-legacy-shape");
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) seed = await data.UnsealedAsync(
            moduleKey: ClassicContentContract.ModuleKey,
            inputVersion: inputVersion);
        SnapshotInfo before = await seed.Writer.SealAsync(default);
        ISnapshotReader reader = data.Store.SnapshotReader(seed.Assessment, seed.Snapshot);
        byte[] originalBytes = (await reader.ReadAsync(before.ObservationIds[0], default)).Artifact.GetBytes()!;
        ForbiddenOnlineEnvironment environment = new() { ForbidRestore = true };
        PipelineCoordinator coordinator = new(data.Store, ClassicPageModule.Registry(), environment);
        NotSupportedException failure = await Assert.ThrowsAsync<NotSupportedException>(() =>
            coordinator.AnalyzeAsync(
                new() { Id = seed.Assessment.ToString(), SnapshotId = seed.Snapshot.ToString(), }));
        Assert.Contains(inputVersion, failure.Message);
        SnapshotInfo after = await reader.OpenAsync(default);
        Assert.Equal(before.ManifestDigest, after.ManifestDigest);
        Assert.Equal(before.InputVersion, after.InputVersion);
        Assert.Equal(originalBytes, (await reader.ReadAsync(before.ObservationIds[0], default)).Artifact.GetBytes());
        Assert.Equal(0, environment.OnlineRequests + environment.RestoreRequests + environment.ProtectionRequests);
        using ScanContext db = data.Store.CreateContext(seed.Assessment);
        Assert.Empty(await db.AnalysisRuns.ToListAsync());
        Assert.Single(await db.PhaseRuns.ToListAsync());
        Assert.Throws<NotSupportedException>(() =>
            ClassicPageModule.Registry()
                .GetAnalyzer(ClassicContentContract.ModuleKey, "classicpage-v2", ClassicContentContract.InputVersion));
    }

    /// <summary>
    /// Starts the production collector and analyzer with recorded transport and unchanged original Classic options.
    /// </summary>
    private static async Task<TestExecution> StartAsync(
        StoreCase data,
        ClassicContentFixture source,
        bool homeOnly = false)
    {
        PipelineCoordinator coordinator = new(
            data.Store,
            ClassicPageModule.Registry(source.OpenAsync),
            new ForbiddenOnlineEnvironment());
        PhaseReply ticket = await coordinator.StartPipelineAsync(
            new() { Collection = ClassicContentFixture.Request(homeOnly: homeOnly) });
        return new(coordinator, ticket, Guid.Parse(ticket.AssessmentId));
    }

    /// <summary>
    /// Includes the actual pipeline error in status assertions so a failed collection cannot masquerade as an empty result.
    /// </summary>
    private static async Task AssertStatusAsync(StoreCase data, TestExecution execution, ScanStatus expected)
    {
        ScanStatus actual = await execution.Coordinator.WaitForCompletionAsync(execution.AssessmentId)
            .WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(
            actual == expected,
            $"Expected {expected}, received {actual}: {(await data.Store.LatestRootAsync(execution.AssessmentId))!.LastError}");
    }

    /// <summary>
    /// Reads the immutable unified Page publication rather than treating legacy URL-keyed rows as the native object model.
    /// </summary>
    private static async Task<ClassicCompatibilityPayload> PublicationAsync(StoreCase data, Guid assessmentId)
    {
        using ScanContext db = data.Store.CreateContext(assessmentId);
        AnalysisReportRowEntry row = await db.AnalysisReportRows.SingleAsync();
        return new VersionedJson(row.PayloadJson).Value.Deserialize<ClassicCompatibilityPayload>()!;
    }

    /// <summary>
    /// Wraps an acquired receipt for metadata inspection without changing its original bytes.
    /// </summary>
    private static SourceRecord ToRecord(SourceObservationRow row) =>
        new(
            row.ObservationId,
            row.SnapshotId,
            row.SourceIdentity,
            row.SourceRevision,
            row.AcquisitionStatus,
            new VersionedJson(row.MetadataJson),
            new SourceArtifact(row.RawBytes?.LongLength, null, row.RawBytes),
            row.AcquisitionError);

    /// <summary>
    /// Keeps the production execution handle and native assessment identity together for bounded recorded tests.
    /// </summary>
    private sealed record TestExecution(PipelineCoordinator Coordinator, PhaseReply Ticket, Guid AssessmentId);
}