#nullable enable
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Analysis.Module;
using PnP.Scanning.Core.Pipeline.Analysis.Page;
using PnP.Scanning.Core.Pipeline.Collection.File;
using PnP.Scanning.Core.Pipeline.Collection.List;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.List;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Page;
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
    /// Verifies independent ASPX and pointpub acquisition in one library converges on physical-file identity without collapsing Posts.
    /// </summary>
    [Fact]
    public async Task Aspx_and_pointpub_share_a_library_but_only_native_file_links_unify_the_item_and_inventory()
    {
        using StoreCase data = new("classic-native-content");
        ClassicContentFixture source = new() { IncludeBlog = true, IncludeSecondBlog = true, IncludeDelve = true };
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
        ClassicPageRow pointPublishing = Assert.Single(
            publication.Pages,
            page => page.FileUniqueId == ClassicContentFixture.PointPublishingFileId);
        Assert.Equal(
            PageIdentity.File(ClassicContentFixture.SiteCollectionId, ClassicContentFixture.PointPublishingFileId),
            pointPublishing.Identity);
        Assert.Equal(ClassicContentFixture.ListId, pointPublishing.ListId);
        Assert.Equal(200, pointPublishing.ListItemId);
        Assert.Equal("NotAttempted", pointPublishing.ContentReadState);
        Assert.Equal("File", pointPublishing.SourceKind);
        Assert.Contains(
            publication.Pages,
            page => page.ListId == pointPublishing.ListId && page.PageUrl?.EndsWith(".aspx") == true);
        Assert.Single(publication.Files, file => file.FileUniqueId == pointPublishing.FileUniqueId);
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
        SourceObservationRow receipt = (await db.SourceObservations.ToListAsync()).Single(row =>
            CollectionJson.Kind(ToRecord(row)) == "ListItems" &&
            CollectionJson.Read<ListItemBatch>(row.RawBytes).QueryKind == ListItemQueryKind.PointPublishingFiles);
        ListItemFacts item = Assert.Single(CollectionJson.Read<ListItemBatch>(receipt.RawBytes).Items);
        Assert.Equal(pointPublishing.FileUniqueId, item.FileAssociation!.FileFacts!.FileUniqueId);
        Assert.Null(item.FileAssociation.FileFacts.AspxEvidenceJson);
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
        ClassicContentFixture source = new() { IncludeDelve = true };
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
            source.TransformBatch = batch =>
            {
                batch.Items[0].FileAssociation!.FileFacts!.FileUniqueId =
                    Guid.Parse("00000000-0000-0000-0000-000000000001");
                return batch;
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

    /// <summary>
    /// Verifies the two native file extensions have independent acquisition filters rather than exclusive final page types.
    /// </summary>
    [Fact]
    public void Query_filters_name_acquired_file_objects_and_do_not_classify_the_library_as_a_page_family()
    {
        string pointPublishing = ListItemQueryBuilder.Create(
            [],
            filterOnAspxFiles: false,
            filterOnPointPublishingFiles: true);
        string aspx = ListItemQueryBuilder.Create([]);
        string posts = ListItemQueryBuilder.Create([], filterOnAspxFiles: false);
        Assert.Contains("<Value Type='Text'>pointpub</Value>", pointPublishing);
        Assert.DoesNotContain("<Value Type='text'>aspx</Value>", pointPublishing);
        Assert.Contains("<Value Type='text'>aspx</Value>", aspx);
        Assert.DoesNotContain("pointpub", aspx);
        Assert.DoesNotContain("<Where>", posts);
        Assert.Contains("<RowLimit Paged='TRUE'>1000</RowLimit>", pointPublishing);
    }

    /// <summary>
    /// Verifies denied and missing native file links remain explicit unknown item evidence without creating item Pages.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unproven_pointpub_file_links_are_unknown_evidence_not_fabricated_pages(bool denied)
    {
        using StoreCase data = new("classic-unproven-file");
        ClassicContentFixture source = new() { IncludeDelve = true };
        source.TransformInventory = inventory => inventory with
        {
            Files = inventory.Files!.Where(file => file.FileUniqueId != ClassicContentFixture.PointPublishingFileId)
                .ToArray(),
        };
        source.TransformBatch = batch => batch with
        {
            Items = batch.Items.Select(item => item with
                {
                    FileAssociation = denied ? new(new("Denied", "Synthetic native File denial"), null) : null,
                })
                .ToArray(),
        };
        TestExecution execution = await StartAsync(data, source);
        await AssertStatusAsync(data, execution, ScanStatus.Finished);
        ClassicCompatibilityPayload publication = await PublicationAsync(data, execution.AssessmentId);
        Assert.DoesNotContain(
            publication.Pages,
            page => page.ListItemId == 200 || page.Identity!.ObjectKind == PageObjectKind.ListItem);
        using ScanContext db = data.Store.CreateContext(execution.AssessmentId);
        AnalysisResultRow result = (await db.AnalysisResults.ToListAsync()).Single(row =>
            new VersionedJson(row.PayloadJson).Value.GetProperty("kind").GetString() == "ListItems");
        Assert.Equal(AnalysisOutcome.Unknown, result.Outcome);
        Assert.Equal(1, new VersionedJson(result.PayloadJson).Value.GetProperty("unresolvedFileLinks").GetInt32());
        SourceObservationRow receipt =
            (await db.SourceObservations.ToListAsync()).Single(row =>
                CollectionJson.Kind(ToRecord(row)) == "ListItems");
        Assert.Equal(AcquisitionStatus.Partial, receipt.AcquisitionStatus);
        Assert.Single(CollectionJson.Read<ListItemBatch>(receipt.RawBytes).Items);
        Assert.Contains(
            await db.ClassicPageDiscoveries.ToListAsync(),
            row => row.ErrorStage == "NativeFileAssociation" && row.ListItemId == 200 && row.FileUniqueId == null);
        Assert.Equal(
            denied ? "Incomplete" : "Unknown",
            (await db.ClassicPageDiscoveries.SingleAsync(row => row.RecordKey == "summary:coverage")).DiscoveryStatus);
    }

    /// <summary>
    /// Verifies native File relationship validation never guesses identity from a matching URL, item ID, or missing GUID.
    /// </summary>
    [Theory]
    [InlineData("file")]
    [InlineData("site")]
    [InlineData("web")]
    [InlineData("list")]
    [InlineData("item")]
    [InlineData("locator")]
    public void Native_file_relationship_requires_each_observed_identity_and_consistent_locators(string conflict)
    {
        ClassicContentFixture source = new();
        ListItemFacts item = new(
            7,
            new()
            {
                ["ID"] = SourceField.Of("Int32", 7),
                ["FileRef"] = SourceField.Of("String", "/sites/a/Stories/a.pointpub"),
            });
        NativeItemFile observed = new(
            ClassicContentFixture.SiteCollectionId,
            ClassicContentFixture.WebId,
            ClassicContentFixture.ListId,
            7,
            ClassicContentFixture.PointPublishingFileId,
            "a.pointpub",
            "/sites/a/Stories/a.pointpub",
            99,
            "Customized");
        NativeItemFile invalid = conflict switch
        {
            "file" => observed with { FileUniqueId = Guid.Empty },
            "site" => observed with { SiteCollectionId = Guid.NewGuid() },
            "web" => observed with { WebId = Guid.NewGuid() },
            "list" => observed with { ListId = Guid.NewGuid() },
            "item" => observed with { ListItemId = 8 },
            "locator" => observed with { ServerRelativeUrl = "/sites/a/Stories/b.pointpub" },
            _ => throw new ArgumentOutOfRangeException(nameof(conflict)),
        };
        Assert.Throws<SnapshotIntegrityException>(() => NativeFileAssociationReader.FromNative(
            Guid.NewGuid(),
            source.Web(),
            ClassicContentFixture.ListId,
            item,
            invalid));
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
        ClassicContentFixture source = new() { IncludeBlog = true, IncludeDelve = true };
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