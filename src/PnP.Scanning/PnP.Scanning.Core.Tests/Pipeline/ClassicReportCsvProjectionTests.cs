#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.ClassicPages.Compatibility;
using PnP.Scanning.Core.ClassicPages.Contracts.Audit;
using PnP.Scanning.Core.ClassicPages.Contracts.Module;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.ClassicPages.Contracts.Site;
using PnP.Scanning.Core.ClassicPages.Contracts.Web;
using PnP.Scanning.Core.Pipeline.Collection.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>Locks the raw ASPX inventory and exact native Pages predicate independently of the legacy report table.</summary>
public sealed class ClassicReportCsvProjectionTests
{
    private const string Site = "https://contoso.invalid/sites/csv";
    private static readonly Guid SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WebId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ListId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly string[] Handlers =
    [
        "Microsoft.SharePoint.WebPartPages.WikiEditPage", "Microsoft.SharePoint.WebPartPages.WebPartPage",
        "Microsoft.SharePoint.Publishing.TemplateRedirectionPage", "System.Web.UI.Page",
    ];

    /// <summary>
    /// Proves the selection against an independent SQL predicate: NoExecute null/false is selected and true is excluded,
    /// a registered list view or form is excluded while unknown list role is selected, and catalog role has no effect.
    /// </summary>
    [Fact]
    public async Task Native_pages_equal_the_SQL_predicate_for_every_handler_NoExecute_and_role_combination()
    {
        Guid assessment = Guid.NewGuid();
        List<SPFile> files = [];
        foreach (string handler in Handlers.Append("Contoso.WikiEditPage")
                     .Append("Microsoft.SharePoint.WebControls.ClientSidePage"))
        foreach (bool? blocked in new bool?[] { true, false, null })
        foreach (bool? catalog in new bool?[] { true, false, null })
        foreach (bool? view in new bool?[] { true, false, null })
        foreach (bool? form in new bool?[] { true, false, null })
            files.Add(File(assessment, files.Count + 1, handler, blocked, catalog, view, form));
        ClassicReportSource source = Source(files.ToArray());
        ClassicPageCsvRow[] actual = ClassicReportCsvProjection.SelectPages(source);
        using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE CapturedAspx(FileUniqueId TEXT, PageHandler TEXT, NoExecute INTEGER, " +
                             "IsListView INTEGER, IsListForm INTEGER)";
        await create.ExecuteNonQueryAsync();
        foreach (SPFile file in files)
        {
            AspxCsvRow raw = new(file);
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO CapturedAspx VALUES ($id,$handler,$blocked,$view,$form)";
            insert.Parameters.AddWithValue("$id", file.FileUniqueId.ToString());
            insert.Parameters.AddWithValue("$handler", raw.PageHandler);
            insert.Parameters.AddWithValue(
                "$blocked",
                raw.NoExecute switch
                {
                    true => 1,
                    false => 0,
                    null => DBNull.Value
                });
            insert.Parameters.AddWithValue(
                "$view",
                raw.IsListView switch
                {
                    true => 1,
                    false => 0,
                    null => DBNull.Value
                });
            insert.Parameters.AddWithValue(
                "$form",
                raw.IsListForm switch
                {
                    true => 1,
                    false => 0,
                    null => DBNull.Value
                });
            await insert.ExecuteNonQueryAsync();
        }

        using SqliteCommand query = connection.CreateCommand();
        query.CommandText = "SELECT FileUniqueId FROM CapturedAspx WHERE NoExecute IS NOT 1 " +
                            "AND IsListView IS NOT 1 AND IsListForm IS NOT 1 AND PageHandler IN (" +
                            "'Microsoft.SharePoint.WebPartPages.WikiEditPage'," +
                            "'Microsoft.SharePoint.WebPartPages.WebPartPage'," +
                            "'Microsoft.SharePoint.Publishing.TemplateRedirectionPage'," +
                            "'System.Web.UI.Page') ORDER BY FileUniqueId";
        List<Guid> expected = [];
        using SqliteDataReader reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) expected.Add(Guid.Parse(reader.GetString(0)));
        Assert.Equal(expected.Order(), actual.Select(page => page.FileUniqueId!.Value).Order());
        Assert.Equal(96, actual.Length);
        Assert.DoesNotContain(actual, page => page.IsListView == true || page.IsListForm == true);
        Assert.Contains(
            actual,
            page => page.NoExecute == null && page.IsCatalog == true && page.IsListView == null &&
                    page.IsListForm == null);
        Assert.Contains(actual, page => page.NoExecute == false && page.RoleReadState == "Partial");
        Assert.All(actual, page => Assert.Equal("MetadataAssessmentType", page.AssessmentPageType));
        Assert.All(actual, page => Assert.Null(page.Official));
        Assert.All(actual, page => Assert.Null(page.WebPartCount));
        Assert.Equal(files.Count, source.Publication.Files.Length);
    }

    /// <summary>
    /// A registered list view or form is the List product family and stays in aspx.csv only; unknown list role is selected.
    /// The selection agrees with the classifier's own List family verdict.
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, null, false)]
    [InlineData(null, true, false)]
    [InlineData(false, false, true)]
    [InlineData(null, null, true)]
    [InlineData(null, false, true)]
    [InlineData(false, null, true)]
    public void List_product_family_files_stay_outside_the_page_selection(bool? view, bool? form, bool selected)
    {
        ClassicReportSource source = Source([File(Guid.NewGuid(), 1, Handlers[1], false, false, view, form)]);
        Assert.Equal(selected ? 1 : 0, ClassicReportCsvProjection.SelectPages(source).Length);
        string family = PnP.Scanning.Core.ClassicPages.Analysis.Page.AspxPageClassifier.Classify(
                Handlers[1],
                119,
                view,
                form,
                false)
            .ProductFamily;
        Assert.Equal(!selected, family == "List");
    }

    /// <summary>A source with no Inherits declaration resolves to System.Web.UI.Page and is selected as an ASPXPage.</summary>
    [Fact]
    public void A_page_without_an_Inherits_declaration_is_selected_as_System_Web_UI_Page()
    {
        SPFile file = File(Guid.NewGuid(), 1, Handlers[3], false, false);
        AspxPageEvidence evidence = JsonSerializer.Deserialize<AspxPageEvidence>(file.AspxEvidenceJson!)!;
        file.AspxEvidenceJson = JsonSerializer.Serialize(
            evidence with
            {
                PageHandler = null,
                OriginalBytes = Encoding.UTF8.GetBytes("<%@ Page Language=\"C#\" %>")
            });
        ClassicPageCsvRow row = Assert.Single(ClassicReportCsvProjection.SelectPages(Source([file])));
        Assert.Equal("System.Web.UI.Page", row.PageHandler);
        Assert.Equal("DefaultAssumed", row.HandlerResolution);
        Assert.Equal("ASPXPage", row.PageType);
    }

    /// <summary>Requires exact FQN handlers and never admits short aliases, suffixes, legacy labels or unavailable source.</summary>
    [Theory]
    [InlineData("WikiEditPage")]
    [InlineData("wiki")]
    [InlineData("WikiPage")]
    [InlineData("Microsoft.SharePoint.WebPartPages.WikiEditPageExtra")]
    [InlineData("system.web.ui.page")]
    [InlineData(null)]
    public void Handler_admission_never_guesses_a_type(string? handler) =>
        Assert.Null(ClassicReportCsvProjection.PageTypeForHandler(handler));

    /// <summary>Posts remain independent of renderer NoExecute/source and never inherit renderer-only usage or default zero metrics.</summary>
    [Fact]
    public void Posts_are_not_subject_to_file_gates_or_renderer_usage()
    {
        Guid assessment = Guid.NewGuid();
        SPFile renderer = File(assessment, 1, Handlers[1], true, true);
        renderer.ServerRelativeUrl = "/sites/csv/Lists/Posts/Post.aspx";
        ClassicReportSource source = Source([renderer], usage: true);
        ClassicPageRow first = Post(assessment, ListId, 10);
        ClassicPageRow second = Post(assessment, Guid.Parse("44444444-4444-4444-4444-444444444444"), 10);
        second.PageUrl = "/sites/csv/Lists/OtherPosts/Post.aspx?ID=10";
        source = source with
        {
            Publication = source.Publication with
            {
                Pages = [.. source.Publication.Pages, first, second],
                Audit =
                [
                    new("https://contoso.invalid/sites/csv/Lists/Posts/Post.aspx", 99, 0, 0, ["renderer-user"]),
                    new("https://contoso.invalid" + first.PageUrl, 3, 1, 2, ["post-user"])
                ],
            }
        };
        ClassicPageCsvRow[] rows = ClassicReportCsvProjection.SelectPages(source);
        Assert.Equal(2, rows.Length);
        ClassicPageCsvRow post = Assert.Single(rows, row => row.ListId == ListId);
        Assert.Null(post.NoExecute);
        Assert.Null(post.FileUniqueId);
        Assert.Null(post.PageHandler);
        Assert.Null(post.WebPartCount);
        Assert.Equal(3, post.AuditViewsCount);
        Assert.Equal(1, post.AuditUniqueUsers);
        ClassicPageCsvRow unobserved = Assert.Single(rows, row => row.ListId != ListId);
        Assert.Null(unobserved.AuditViewsCount);
        Assert.Equal("ItemLocatorNotObserved", unobserved.UsageAttributionState);
        Assert.NotEqual(rows[0].ContentKey, rows[1].ContentKey);
    }

    /// <summary>Unknown, skipped and failed usage stay null rather than appearing as a successful zero count.</summary>
    [Theory]
    [InlineData("skipped")]
    [InlineData("failed")]
    [InlineData("notcollected")]
    public void Unavailable_usage_is_nullable(string status)
    {
        ClassicReportSource source = Source(
            [File(Guid.NewGuid(), 1, Handlers[0], null, null)],
            usage: status != "skipped");
        source = source with
        {
            AuditChunks = status == "failed"
                ? [new(0, DateTime.UnixEpoch, DateTime.UnixEpoch.AddDays(1), "query", "failed", "Recorded denial")]
                : []
        };
        ClassicPageCsvRow row = Assert.Single(ClassicReportCsvProjection.SelectPages(source));
        Assert.Equal(status, row.UsageQueryStatus);
        Assert.Null(row.AuditViewsCount);
        Assert.Null(row.AuditUniqueUsers);
    }

    /// <summary>Retains successful subwindow counts as explicitly partial rather than complete or wholly unavailable usage.</summary>
    [Fact]
    public void Partial_usage_retains_only_observed_counts_and_its_incomplete_window_state()
    {
        ClassicReportSource source = Source([File(Guid.NewGuid(), 1, Handlers[0], false, false)], usage: true);
        source = source with
        {
            AuditChunks =
            [
                .. source.AuditChunks,
                new(1, DateTime.UnixEpoch.AddDays(1), DateTime.UnixEpoch.AddDays(2), "denied", "failed", "403")
            ],
            Publication = source.Publication with
            {
                Audit = [new("https://contoso.invalid/sites/csv/1.aspx", 2, 0, 1, ["one"])]
            },
        };
        ClassicPageCsvRow row = Assert.Single(ClassicReportCsvProjection.SelectPages(source));
        Assert.Equal("partial", row.UsageQueryStatus);
        Assert.Equal(2, row.AuditViewsCount);
        Assert.Equal(1, row.AuditEditsCount);
        Assert.Equal(1, row.AuditUniqueUsers);
        source = source with
        {
            AuditChunks =
            [
                new(0, DateTime.UnixEpoch, DateTime.UnixEpoch.AddDays(1), null, "skipped", "Unsupported cloud")
            ]
        };
        row = Assert.Single(ClassicReportCsvProjection.SelectPages(source));
        Assert.Equal("skipped", row.UsageQueryStatus);
        Assert.Null(row.AuditViewsCount);
    }

    /// <summary>The indexed usage join keeps path-case, absolute/server-relative and exact query-suffix semantics.</summary>
    [Fact]
    public void Indexed_usage_join_preserves_full_locator_semantics_and_unique_users()
    {
        Guid assessment = Guid.NewGuid();
        ClassicReportSource source = Source(
            [File(assessment, 1, Handlers[0], false, false), File(assessment, 2, Handlers[0], false, false)],
            usage: true);
        source = source with
        {
            Publication = source.Publication with
            {
                Audit =
                [
                    new("https://contoso.invalid/sites/csv/1.aspx", 2, 0, 1, ["one", "two"]),
                    new("HTTPS://CONTOSO.INVALID/SITES/CSV/1.ASPX", 1, 1, 0, ["two", "three"]),
                    new("/sites/csv/1.aspx", 4, 0, 0, ["three", "four"]),
                    new("https://contoso.invalid/sites/csv/1.aspx?ID=7", 50, 0, 0, ["query-user"]),
                    new("https://other.invalid/sites/csv/1.aspx", 60, 0, 0, ["foreign-user"])
                ]
            },
        };
        ClassicPageCsvRow[] rows = ClassicReportCsvProjection.SelectPages(source);
        ClassicPageCsvRow matched = Assert.Single(rows, row => row.AuditViewsCount == 7);
        Assert.Equal(1, matched.AuditCreatesCount);
        Assert.Equal(1, matched.AuditEditsCount);
        Assert.Equal(4, matched.AuditUniqueUsers);
        Assert.Equal("ExactContentLocator", matched.UsageAttributionState);
        ClassicPageCsvRow silent = Assert.Single(rows, row => row != matched);
        Assert.Equal(0, silent.AuditViewsCount);
        Assert.Equal(0, silent.AuditUniqueUsers);
        Assert.Equal("CoveredFileNoEvents", silent.UsageAttributionState);
        Assert.All(rows, row => Assert.Equal("succeeded", row.UsageQueryStatus));
    }

    /// <summary>Distinct native identities and identical source hashes remain separate; legacy URL collisions still fail explicitly.</summary>
    [Fact]
    public void Native_identity_is_not_URL_or_hash_deduplication()
    {
        Guid assessment = Guid.NewGuid();
        SPFile first = File(assessment, 1, Handlers[3], false, false);
        SPFile second = File(assessment, 2, Handlers[3], false, false);
        ClassicReportSource source = Source([first, second]);
        ClassicPageCsvRow[] selected = ClassicReportCsvProjection.SelectPages(source);
        Assert.Equal(2, selected.Length);
        Assert.Equal(selected[0].SourceSha256, selected[1].SourceSha256);
        source.Publication.Pages[1].PageUrl = source.Publication.Pages[0].PageUrl;
        Assert.Throws<SnapshotIntegrityException>(() => ClassicReportCsvProjection.SelectPages(source));
    }

    /// <summary>Preserves the shared nullable-text wire contract used by the physical ASPX inventory.</summary>
    [Theory]
    [InlineData(null, @"\N")]
    [InlineData("", "")]
    [InlineData(@"\N", @"\\N")]
    [InlineData(@"\\N", @"\\\N")]
    [InlineData(@"\leading", @"\\leading")]
    [InlineData(@"inside\path", @"inside\path")]
    [InlineData("quoted,\"value\"\r\nline", "quoted,\"value\"\r\nline")]
    public void Aspx_nullable_text_encoding_is_reversible(string? value, string encoded)
    {
        Assert.Equal(encoded, ClassicAspxNullableStringConverter.Encode(value));
        Assert.Equal(value, ClassicAspxNullableStringConverter.Decode(encoded));
    }

    /// <summary>Raw export streams every SQL ASPX fact, including modern/blocked/unknown/denied/catalog files, preserving literal bytes and null strings.</summary>
    [Fact]
    public async Task Raw_Aspx_CSV_is_unfiltered_SQL_inventory_with_reversible_nulls_and_matching_counts()
    {
        using StoreCase data = new("raw-aspx-csv");
        (Guid Assessment, Guid Snapshot, Guid Run, ISnapshotWriter Writer) seed = await data.UnsealedAsync();
        SPFile[] files =
        [
            File(seed.Assessment, 1, Handlers[0], true, true), File(seed.Assessment, 2, Handlers[1], false, false),
            File(seed.Assessment, 3, "Microsoft.SharePoint.WebControls.ClientSidePage", null, null),
            File(seed.Assessment, 4, "Contoso.Unknown", null, true), File(seed.Assessment, 5, null, null, null),
        ];
        files[4].FileErrorDetail = "403\r\noriginal, evidence";
        files[4].FileReadState = "Denied";
        files[4].AspxEvidenceJson = null;
        files[4].ListId = null;
        files[4].ListItemId = null;
        files[3].SetupPath = "";
        using ScanContext context = data.Store.CreateContext(seed.Assessment);
        context.SPFiles.AddRange(files);
        SPFile nonAspx = File(seed.Assessment, 6, Handlers[0], false, false);
        nonAspx.ServerRelativeUrl = "/sites/csv/a.pointpub";
        nonAspx.FileName = "a.pointpub";
        context.SPFiles.Add(nonAspx);
        await context.SaveChangesAsync();
        string directory = Path.Combine(data.DirectoryPath, "export");
        Directory.CreateDirectory(directory);
        CsvConfiguration configuration = new(CultureInfo.InvariantCulture) { Delimiter = ";" };
        await ClassicReportCsvProjection.ExportAspxAsync(context, seed.Assessment, directory, configuration);
        using StreamReader text = new(Path.Combine(directory, "aspx.csv"));
        using CsvReader csv = new(text, configuration);
        csv.Context.TypeConverterCache.AddConverter<string>(new ClassicAspxNullableStringConverter());
        await csv.ReadAsync();
        csv.ReadHeader();
        Assert.Equal(
            ("ScanId,SiteCollectionId,FileUniqueId,WebId,ListId,FolderUniqueId,ListItemId,Length," +
             "SiteUrl,WebUrl,ServerRelativeUrl,FileName,ContentTypeId,CustomizedPageStatus,SetupPath,FileReadState," +
             "FileErrorCodes,FileErrorDetail,ProvenanceJson,CoverageState,FirstObservedAtUtc,ObservedAtUtc," +
             "ClientSideApplicationId,HtmlFileType,FileType,BSN,WikiFieldHasValue,ItemMetadataReadState," +
             "ItemMetadataFieldStatesJson,NoExecuteRaw,NoExecuteSource,NoExecuteReadState,AspxEvidenceJson,NoExecute," +
             "PageHandler,SourceSha256,SourceReadState,HandlerResolution,PageDeclaration,SourceError,ListTemplate," +
             "IsCatalog,IsListView,IsListForm,RoleReadState,Official,OfficialReadState").Split(','),
            csv.HeaderRecord);
        List<Guid> returned = [];
        while (await csv.ReadAsync())
        {
            Guid id = csv.GetField<Guid>("FileUniqueId");
            SPFile original = files.Single(file => file.FileUniqueId == id);
            returned.Add(id);
            Assert.Equal(original.AspxEvidenceJson, csv.GetField<string>("AspxEvidenceJson"));
            Assert.Equal(original.SetupPath, csv.GetField<string>("SetupPath"));
            Assert.Equal(original.NoExecuteRaw, csv.GetField<string>("NoExecuteRaw"));
        }

        Assert.Equal(files.Select(file => file.FileUniqueId).Order(), returned.Order());
        using JsonDocument summary = JsonDocument.Parse(
            await System.IO.File.ReadAllTextAsync(Path.Combine(directory, "aspxsummary.json")));
        Assert.Equal(5, summary.RootElement.GetProperty("ExportedAspxCount").GetInt64());
        Assert.Equal(1, summary.RootElement.GetProperty("NoExecuteTrueCount").GetInt64());
        Assert.Equal(1, summary.RootElement.GetProperty("NoExecuteFalseCount").GetInt64());
        Assert.Equal(3, summary.RootElement.GetProperty("NoExecuteUnknownCount").GetInt64());
    }

    /// <summary>
    /// Proves selected-Page views preserve full SQL/canonical evidence and other components, and reject tampered publications.
    /// </summary>
    [Fact]
    public async Task Native_report_views_preserve_complete_component_and_part_evidence_and_validate_the_publication()
    {
        using StoreCase data = new("native-csv-preservation");
        ClassicContentFixture recorded = new() { IncludeBlog = true };
        StartRequest options = ClassicContentFixture.MakeOptions();
        options.Properties.Clear();
        ClassicStartRequestBuilder.AddClassicProperties(
            options,
            [ClassicComponent.Pages, ClassicComponent.Lists],
            false,
            true,
            false,
            false,
            14);
        PipelineCoordinator coordinator = new(
            data.Store,
            PnP.Scanning.Core.ClassicPages.ClassicPageModule.Registry(recorded.OpenAsync),
            new ForbiddenOnlineEnvironment());
        PhaseReply ticket = await coordinator.StartPipelineAsync(
            new()
            {
                Collection = new()
                {
                    Module = "classicpage",
                    ParametersJson = VersionedJson.Empty.Json,
                    CollectionOptions = options
                },
            });
        Guid assessment = Guid.Parse(ticket.AssessmentId);
        Assert.Equal(ScanStatus.Finished, await coordinator.WaitForCompletionAsync(assessment));
        using ScanContext context = data.Store.CreateContext(assessment);
        int canonicalPages = await context.ClassicPages.CountAsync();
        int capturedParts = await context.ClassicPageWebParts.CountAsync();
        int capturedFiles = await context.SPFiles.CountAsync();
        string immutableReport = (await context.AnalysisReportRows.SingleAsync()).PayloadJson;
        string snapshotDigest = (await context.SourceSnapshots.SingleAsync()).ManifestDigest!;
        Assert.True(capturedParts > 0);
        string directory = Path.Combine(data.DirectoryPath, "export");
        Directory.CreateDirectory(directory);
        await ReportManager.ExportClassicReportDataAsync(
            context,
            assessment,
            directory,
            new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "," });
        Assert.False(System.IO.File.Exists(Path.Combine(directory, "classicaspx.csv")));
        Assert.False(System.IO.File.Exists(Path.Combine(directory, "classicaspxsummary.json")));
        Assert.True(System.IO.File.Exists(Path.Combine(directory, "aspx.csv")));
        Assert.True(System.IO.File.Exists(Path.Combine(directory, "aspxsummary.json")));
        Assert.True(System.IO.File.Exists(Path.Combine(directory, "classicpagessummary.json")));
        Assert.True(System.IO.File.Exists(Path.Combine(directory, "discovery.csv")));
        Assert.False(System.IO.File.Exists(Path.Combine(directory, "classicpageauditusage.csv")));
        Assert.Equal(canonicalPages, await context.ClassicPages.CountAsync());
        Assert.Equal(capturedParts, await context.ClassicPageWebParts.CountAsync());
        Assert.Equal(capturedFiles, await context.SPFiles.CountAsync());
        Assert.Equal(immutableReport, (await context.AnalysisReportRows.SingleAsync()).PayloadJson);
        Assert.Equal(snapshotDigest, (await context.SourceSnapshots.SingleAsync()).ManifestDigest);
        using (StreamReader listText = new(Path.Combine(directory, "classiclists.csv")))
        using (CsvReader lists = new(listText, CultureInfo.InvariantCulture))
            Assert.Single(lists.GetRecords<ClassicList>());
        using (StreamReader pageText = new(Path.Combine(directory, "classicpages.csv")))
        using (CsvReader pages = new(pageText, CultureInfo.InvariantCulture))
        {
            await pages.ReadAsync();
            pages.ReadHeader();
            Assert.All(
                new[]
                {
                    "PageType", "AssessmentPageType", "ObjectKind", "ContentKey", "NoExecute", "PageHandler",
                    "ListItemId", "AnalysisRunId", "SnapshotId", "UsageQueryStatus"
                },
                column => Assert.Contains(column, pages.HeaderRecord!));
            int count = 0;
            while (await pages.ReadAsync())
            {
                Assert.Equal("BlogPage", pages.GetField("PageType"));
                Assert.Null(pages.GetField<int?>("WebPartCount"));
                Assert.Null(pages.GetField<bool?>("NoExecute"));
                Assert.Null(pages.GetField<int?>("AuditViewsCount"));
                count++;
            }

            Assert.Equal(2, count);
        }

        using (StreamReader partText = new(Path.Combine(directory, "classicpagewebparts.csv")))
        using (CsvReader parts = new(partText, CultureInfo.InvariantCulture))
            Assert.Empty(parts.GetRecords<ClassicPageWebPart>());
        using (StreamReader webText = new(Path.Combine(directory, "classicwebsummaries.csv")))
        using (CsvReader webs = new(webText, CultureInfo.InvariantCulture))
        {
            ClassicWebSummary summary = Assert.Single(webs.GetRecords<ClassicWebSummary>());
            Assert.Equal(2, summary.ClassicPages);
            Assert.Equal(1, summary.ClassicLists);
        }

        AnalysisReportRowEntry report = await context.AnalysisReportRows.SingleAsync();
        report.PayloadJson = VersionedJson.From(new { corrupted = true }).Json;
        await context.SaveChangesAsync();
        await Assert.ThrowsAsync<SnapshotIntegrityException>(() =>
            PipelineStore.ReadClassicReportSourceAsync(context, assessment, default));
    }

    /// <summary>Seeds one file whose catalog, list-view and list-form evidence all share the same state.</summary>
    private static SPFile File(Guid assessment, int ordinal, string? handler, bool? blocked, bool? role) =>
        File(assessment, ordinal, handler, blocked, role, role, role);

    /// <summary>Seeds genuine native keys, original source bytes and independent catalog, list-view and list-form evidence.</summary>
    private static SPFile File(
        Guid assessment,
        int ordinal,
        string? handler,
        bool? blocked,
        bool? catalog,
        bool? view,
        bool? form) =>
        new()
        {
            ScanId = assessment,
            SiteCollectionId = SiteId,
            WebId = WebId,
            ListId = ListId,
            FileUniqueId = new Guid(ordinal, 0, 0, new byte[8]),
            ListItemId = ordinal,
            SiteUrl = Site,
            WebUrl = "/",
            ServerRelativeUrl = "/sites/csv/" + ordinal + ".aspx",
            FileName = ordinal + ".aspx",
            FileReadState = "Complete",
            ItemMetadataReadState = "Available",
            NoExecuteRaw = blocked switch
            {
                true => "1",
                false => "0",
                null => null
            },
            NoExecuteReadState = blocked.HasValue ? "Available" : "NotRequested",
            NoExecuteSource = blocked.HasValue ? "SPO.ListItem.NoExecute" : null,
            CoverageState = "Observed",
            FirstObservedAtUtc = DateTime.UnixEpoch,
            ObservedAtUtc = DateTime.UnixEpoch,
            AspxEvidenceJson = handler == null
                ? null
                : JsonSerializer.Serialize(
                    new AspxPageEvidence(
                        "Available",
                        null,
                        handler,
                        "PendingAnalysis",
                        null,
                        null,
                        119,
                        catalog,
                        view,
                        form,
                        catalog.HasValue && view.HasValue && form.HasValue ? "Complete" : "Partial",
                        DateTime.UnixEpoch,
                        Encoding.UTF8.GetBytes("<%@ Page Inherits=\"" + handler + "\" %>"))),
        };

    /// <summary>Builds a native file Page with deliberately different assessment type and unknown readiness metrics.</summary>
    private static ClassicPageRow Page(SPFile file) =>
        new()
        {
            ScanId = file.ScanId,
            SiteCollectionId = file.SiteCollectionId,
            WebId = file.WebId,
            FileUniqueId = file.FileUniqueId,
            ListId = file.ListId,
            ListItemId = file.ListItemId,
            Identity = PageIdentity.File(file.SiteCollectionId, file.FileUniqueId),
            SiteUrl = Site,
            WebUrl = "/",
            PageUrl = file.ServerRelativeUrl,
            PageName = file.FileName,
            PageType = "ASPXPage",
            AssessmentPageType = "MetadataAssessmentType",
            AssessmentStatus = "Unknown",
            DiscoveryStatus = "Discovered",
            SourceKind = "File",
            ProductFamily = "catalog_masterpage",
            Experience = "Unknown",
        };

    /// <summary>Builds an independent Posts identity with the original item-specific display-form locator.</summary>
    private static ClassicPageRow Post(Guid assessment, Guid list, int id) =>
        new()
        {
            ScanId = assessment,
            SiteCollectionId = SiteId,
            WebId = WebId,
            ListId = list,
            ListItemId = id,
            Identity = PageIdentity.Item(SiteId, list, id),
            SourceKind = "ListItem",
            ListTemplate = 301,
            SiteUrl = Site,
            WebUrl = "/",
            PageUrl = "/sites/csv/Lists/Posts/Post.aspx?ID=" + id,
            PageType = "BlogPage",
            AssessmentPageType = "BlogPage",
            AssessmentStatus = "Complete",
            DiscoveryStatus = "Discovered",
        };

    /// <summary>Creates a fixed native publication with optional successful audit coverage and no live services.</summary>
    private static ClassicReportSource Source(SPFile[] files, bool usage = false)
    {
        ClassicCollectionOptions options = new(false, !usage, false, false, 14);
        WebFacts web = new(
            Site,
            "/",
            "STS#0",
            SiteId,
            WebId,
            DateTime.UnixEpoch,
            [],
            [],
            null,
            SourceReadState.NotAttempted,
            null,
            SourceReadState.NotAttempted,
            null,
            0,
            null,
            SourceReadState.NotAttempted,
            SourceReadState.Complete);
        ClassicCollectionScope scope = new(
            [Site],
            [new SiteCollectionScope(Site, [new(Site, "/", "STS#0")], SourceReadState.Complete)],
            true,
            options,
            DateTime.UnixEpoch,
            DateTime.UnixEpoch.AddDays(1),
            []);
        ClassicCompatibilityPayload payload = new(files.Select(Page).ToArray(), [], [], files, [web], scope, [], []);
        return new(
            files[0].ScanId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            payload,
            usage ? [new(0, scope.AuditWindowStart, scope.AuditWindowEnd, "query", "succeeded", null)] : []);
    }
}