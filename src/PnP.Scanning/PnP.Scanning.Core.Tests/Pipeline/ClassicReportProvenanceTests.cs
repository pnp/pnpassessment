#nullable enable
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.ClassicPages;
using PnP.Scanning.Core.ClassicPages.Analysis.Page;
using PnP.Scanning.Core.ClassicPages.Compatibility;
using PnP.Scanning.Core.ClassicPages.Contracts.Page;
using PnP.Scanning.Core.ClassicPages.Contracts.Shared;
using PnP.Scanning.Core.Pipeline.Contracts.Module;
using PnP.Scanning.Core.Pipeline.Orchestration;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Storage.Pipeline;
using Xunit;

namespace PnP.Scanning.Core.Tests.Pipeline;

/// <summary>Exercises acquisition receipts through production analysis/publication and CSV without hand-constructed aggregate statistics.</summary>
public sealed class ClassicReportProvenanceTests
{
    /// <summary>Keeps query-addressed audit events and exact item attribution distinct from renderer activity in every acquisition state.</summary>
    [Theory]
    [InlineData(false, false, "succeeded")]
    [InlineData(true, false, "succeeded")]
    [InlineData(false, true, "succeeded")]
    [InlineData(true, true, "succeeded")]
    [InlineData(false, false, "mixed")]
    [InlineData(false, false, "fragment-pair")]
    [InlineData(true, false, "fragment-pair")]
    [InlineData(false, false, "partial")]
    [InlineData(false, false, "failed")]
    [InlineData(false, false, "skipped")]
    [InlineData(false, false, "intentional-skip")]
    public async Task Raw_audit_body_survives_the_actual_producer_and_native_CSV(
        bool relative,
        bool extraQuery,
        string status)
    {
        using StoreCase data = new("audit-query-provenance");
        string renderer = "/sites/a/Lists/Posts/Post.aspx" + (extraQuery ? "?return=another.aspx" : "");
        string post = renderer + (extraQuery ? "&" : "?") + "ID=100";
        string authority = relative ? "" : "https://contoso.sharepoint.com";
        List<AuditEvent> events = Enumerable.Range(0, 99)
            .Select(_ => new AuditEvent("ClassicPageViewed", authority + renderer, "renderer"))
            .ToList();
        events.AddRange(
            Enumerable.Range(0, 3)
                .Select(index => new AuditEvent(
                    "ClassicPageViewed",
                    (status == "mixed" && index % 2 == 0 ? "" : authority) + post,
                    "one")));
        events.AddRange(
            Enumerable.Range(0, 7)
                .Select(_ => new AuditEvent(
                    "ClassicPageViewed",
                    authority + "/sites/a/Lists/Posts2/Post.aspx?ID=100",
                    "other-list")));
        if (status == "fragment-pair")
        {
            events.AddRange(
                Enumerable.Range(0, 2)
                    .Select(_ => new AuditEvent("ClassicPageViewed", authority + post + "#A", "fragment-A")));
            events.AddRange(
                Enumerable.Range(0, 4)
                    .Select(_ => new AuditEvent("ClassicPageViewed", authority + post + "#B", "fragment-B")));
        }

        events.Add(new("ClassicPageViewed", authority + "/sites/a/not-a-page.txt?next=misleading.aspx", "ignored"));
        events.Add(new("ClassicPageViewed", "https://foreign.invalid/sites/a/Lists/Posts/Post.aspx?ID=100", "foreign"));
        ClassicContentFixture recorded = new()
        {
            IncludeBlog = true,
            IncludeSecondBlog = true,
            PartialAudit = status == "partial",
            AuditStatus =
                status is "partial" or "intentional-skip" or "mixed" or "fragment-pair" ? "succeeded" : status,
            AuditBody =
                JsonSerializer.Serialize(
                    new { value = events },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            TransformQueries = scope => scope with
            {
                Queries = scope.Queries.Select(query =>
                        query.ListId == ClassicContentFixture.PostsListId
                            ? query with { DisplayFormUrl = renderer }
                            : query)
                    .ToArray()
            },
        };
        (Guid assessment, ClassicReportSource source, string directory) =
            await RunAsync(data, recorded, status != "intentional-skip");
        bool observed = status is "succeeded" or "partial" or "mixed" or "fragment-pair";
        if (observed)
        {
            Assert.Equal(99, Assert.Single(source.Publication.Audit, row => row.PageUrl == authority + renderer).Views);
            Assert.Equal(
                3,
                source.Publication.Audit.Where(row => row.PageUrl == authority + post || row.PageUrl == post)
                    .Sum(row => row.Views));
            if (status == "mixed")
            {
                Assert.Contains(source.Publication.Audit, row => row.PageUrl == post);
                Assert.Contains(source.Publication.Audit, row => row.PageUrl == authority + post);
            }

            Assert.DoesNotContain(
                source.Publication.Audit,
                row => row.PageUrl.Contains("not-a-page.txt", StringComparison.Ordinal));
        }
        else
            Assert.Empty(source.Publication.Audit);

        ExportedPage[] pages = ReadPages(directory);
        ExportedPage first = Assert.Single(
            pages,
            page => page.ListItemId == 100 && page.ListId == ClassicContentFixture.PostsListId);
        ExportedPage second = Assert.Single(
            pages,
            page => page.ListItemId == 101 && page.ListId == ClassicContentFixture.PostsListId);
        ExportedPage other = Assert.Single(
            pages,
            page => page.ListItemId == 100 && page.ListId != ClassicContentFixture.PostsListId);
        Assert.Equal(observed ? 3 : null, first.AuditViewsCount);
        Assert.Equal(observed ? 1 : null, first.AuditUniqueUsers);
        Assert.Equal(
            status == "intentional-skip"
                ? "skipped"
                : status is "mixed" or "fragment-pair"
                    ? "succeeded"
                    : status,
            first.UsageQueryStatus);
        Assert.Equal(observed ? "ExactContentLocator" : "NotAcquired", first.UsageAttributionState);
        Assert.Null(second.AuditViewsCount);
        Assert.Null(second.AuditUniqueUsers);
        Assert.Equal(observed ? "ItemLocatorNotObserved" : "NotAcquired", second.UsageAttributionState);
        Assert.Equal(observed ? 7 : null, other.AuditViewsCount);
        using ScanContext context = data.Store.CreateContext(assessment);
        Assert.Equal(ScanStatus.Finished, (await context.Scans.SingleAsync()).Status);
        if (status == "fragment-pair")
        {
            Assert.Equal(
                2,
                (await context.ClassicPageAuditUsages.SingleAsync(row => row.PageUrl == post + "#A")).AuditViewsCount);
            Assert.Equal(
                4,
                (await context.ClassicPageAuditUsages.SingleAsync(row => row.PageUrl == post + "#B")).AuditViewsCount);
            Assert.Equal(
                3,
                (await context.ClassicPageAuditUsages.SingleAsync(row => row.PageUrl == post)).AuditViewsCount);
        }
    }

    /// <summary>Preserves opaque query case/order and rejects invalid paths without folding distinct item addresses together.</summary>
    [Fact]
    public void Actual_audit_reducer_preserves_query_identity_and_checks_only_valid_ASPX_paths()
    {
        AuditEvent[] events =
        [
            new("ClassicPageViewed", "https://contoso.invalid/Post.aspx?ID=1&Source=A", "one"),
            new("ClassicPageViewed", "https://contoso.invalid/POST.ASPX?ID=1&Source=A", "ONE"),
            new("ClassicPageViewed", "https://contoso.invalid/Post.aspx?ID=1&Source=a", "one"),
            new("ClassicPageViewed", "https://contoso.invalid/Post.aspx?Source=A&ID=1", "one"),
            new("ClassicPageViewed", "/sites/a/data.txt?Source=Post.aspx", "ignored"),
            new("ClassicPageViewed", "//foreign.invalid/Post.aspx?ID=1", "ignored"),
            new("ClassicPageViewed", "/\\foreign.invalid/Post.aspx?ID=1", "ignored"),
            new("ClassicPageViewed", "javascript:Post.aspx?ID=1", "ignored"),
        ];
        string body = JsonSerializer.Serialize(
            new { value = events },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        PnP.Scanning.Core.ClassicPages.Analysis.Audit.ClassicAuditPageStats[] stats =
            PnP.Scanning.Core.ClassicPages.Analysis.Audit.ClassicPageAuditAnalysis.Parse(body);
        Assert.Equal(3, stats.Length);
        Assert.Equal(4, stats.Sum(stat => stat.Views));
        Assert.Equal(
            2,
            Assert.Single(stats, stat => stat.PageUrl.EndsWith("?ID=1&Source=A", StringComparison.Ordinal)).Views);
        Assert.All(stats, stat => Assert.Single(stat.UserHashes));
    }

    /// <summary>Resolves relative presentation addresses only under one declared HTTPS authority without changing query case or order.</summary>
    [Fact]
    public void Relative_audit_attribution_rejects_scheme_relative_ambiguous_or_unverified_authorities()
    {
        const string relative = "/sites/a/Lists/Posts/Post.aspx?Source=A&ID=100";
        Assert.Equal(
            "https://contoso.invalid" + relative,
            PipelineStore.AuditCompatibilityLocator(
                relative,
                ["https://contoso.invalid/sites/a", "https://contoso.invalid/sites/b"]));
        Assert.Throws<SnapshotIntegrityException>(() => PipelineStore.AuditCompatibilityLocator(
            relative,
            ["https://contoso.invalid/sites/a", "https://foreign.invalid/sites/a"]));
        Assert.Throws<SnapshotIntegrityException>(() => PipelineStore.AuditCompatibilityLocator(relative, []));
        Assert.Throws<SnapshotIntegrityException>(() =>
            PipelineStore.AuditCompatibilityLocator(relative, ["http://contoso.invalid/sites/a"]));
        Assert.Throws<SnapshotIntegrityException>(() => PipelineStore.AuditCompatibilityLocator(
            "//foreign.invalid/Posts.aspx?ID=100",
            ["https://contoso.invalid/sites/a"]));
        Assert.Equal(
            "/sites/a/Post.aspx?Source=%2fA&ID=100#Opaque",
            PipelineStore.AuditServerRelativeLocator(
                "https://contoso.invalid/sites/a/Post.aspx?Source=%2fA&ID=100#Opaque"));
    }

    /// <summary>Separates an observed zero part count from withheld mapping, and retains genuinely computed zero and 100 percentages.</summary>
    [Theory]
    [InlineData(true, 0, true, null)]
    [InlineData(true, 1, true, 100.0)]
    [InlineData(true, 1, false, 0.0)]
    [InlineData(false, 0, true, 100.0)]
    [InlineData(false, 1, true, 100.0)]
    [InlineData(false, 1, false, 0.0)]
    public async Task Sealed_part_receipts_prove_count_and_mapping_availability_independently(
        bool fileOnly,
        int partCount,
        bool mapped,
        double? expectedMapping)
    {
        using StoreCase data = new("part-mapping-provenance");
        string key = fileOnly ? "page:6" : "page:2";
        ClassicContentFixture recorded = new()
        {
            TransformInventory = inventory => InventoryWithSource(inventory, key, fileOnly),
            TransformEnrichment = input =>
            {
                if (input.DiscoveryKey != key) return input;
                if (input.FileFacts != null) input.FileFacts.NoExecuteRaw = "0";
                return input with
                {
                    Fields =
                    fileOnly
                        ? input.Fields
                        : new Dictionary<string, SourceField>(input.Fields)
                        {
                            ["NoExecute"] = SourceField.Of("String", "0")
                        },
                    WebPartsState = SourceReadState.Complete,
                    WebParts = partCount == 0 ? [] : [Part(mapped)],
                };
            },
        };
        (Guid assessment, ClassicReportSource source, string directory) = await RunAsync(data, recorded);
        Guid fileId = Guid.Parse("00000000-0000-0000-0000-" + (fileOnly ? "000000000006" : "000000000002"));
        ClassicPageRow canonical = Assert.Single(source.Publication.Pages, page => page.FileUniqueId == fileId);
        ExportedPage row = Assert.Single(ReadPages(directory), page => page.FileUniqueId == fileId);
        Assert.Equal(partCount, row.WebPartCount);
        Assert.Equal(expectedMapping, row.MappingPercentage);
        Assert.Equal(expectedMapping.HasValue ? "Computed" : "WithheldFileOnlyEmpty", row.MappingReadState);
        Assert.Equal(expectedMapping ?? 0, canonical.MappingPercentage);
        Assert.Equal("Complete", canonical.AssessmentStatus);
        using ScanContext context = data.Store.CreateContext(assessment);
        Assert.Equal(
            canonical.MappingPercentage,
            (await context.ClassicPages.SingleAsync(page => page.FileUniqueId == fileId)).MappingPercentage);
    }

    /// <summary>Retains trustworthy true/false home fallback results without treating a failed fallback or default Boolean as measured.</summary>
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, null)]
    [InlineData(false, true, true, null)]
    [InlineData(false, false, true, null)]
    public async Task Primary_home_API_denial_preserves_only_complete_fallback_results(
        bool webFallback,
        bool fileFallback,
        bool unmodified,
        bool? expected)
    {
        using StoreCase data = new("home-fallback-provenance");
        string defaultHtml =
            typeof(HomePageDetector).GetField("DefaultRootHtml", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetRawConstantValue() is string captured
                ? captured
                : throw new InvalidOperationException("The existing synthetic default-home fixture is unavailable.");
        ClassicContentFixture recorded = new()
        {
            TransformInventory = inventory => InventoryWithSource(inventory, "page:1", false),
            TransformWebFacts = web => web with
            {
                CanModernizeHomepage = null,
                CanModernizeState = new("Denied", "Recorded primary API 403"),
                HomeFallbackState =
                webFallback ? SourceReadState.Complete : new("Failed", "Recorded web fallback failure"),
                MasterUrl = "/sites/a/_catalogs/masterpage/seattle.master",
                LocalizedHomePageResource = "Home",
            },
            TransformEnrichment = input =>
            {
                if (input.DiscoveryKey != "page:1") return input;
                Dictionary<string, SourceField> fields = new(input.Fields)
                {
                    ["WikiField"] = SourceField.Of("String", unmodified ? defaultHtml : "<div>Customized</div>"),
                    ["HTML_x0020_File_x0020_Type"] = SourceField.Of("String", "SharePoint.WebPartPage.Document"),
                    ["NoExecute"] = SourceField.Of("String", "0"),
                };
                if (input.FileFacts != null) input.FileFacts.NoExecuteRaw = "0";
                return input with
                {
                    Fields = fields,
                    WebPartsState = SourceReadState.Complete,
                    WebParts =
                    [
                        Part(true) with
                        {
                            ExportXml = Part(true).ExportXml!.Replace(
                                "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart",
                                "Microsoft.SharePoint.WebPartPages.XsltListViewWebPart",
                                StringComparison.Ordinal)
                        }
                    ],
                    HomeFallbackState =
                    fileFallback ? SourceReadState.Complete : new("Failed", "Recorded file fallback failure"),
                    ContentTypeDisplayFormTemplateName = "WikiEditForm",
                };
            },
        };
        (Guid assessment, ClassicReportSource source, string directory) = await RunAsync(data, recorded);
        Guid fileId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        ClassicPageRow canonical = Assert.Single(source.Publication.Pages, page => page.FileUniqueId == fileId);
        ExportedPage exported = Assert.Single(ReadPages(directory), page => page.FileUniqueId == fileId);
        Assert.Equal(expected, exported.UncustomizedHomePage);
        if (expected.HasValue) Assert.Equal(expected.Value, canonical.UncustomizedHomePage);
        using ScanContext context = data.Store.CreateContext(assessment);
        Assert.Equal(
            canonical.UncustomizedHomePage,
            (await context.ClassicPages.SingleAsync(page => page.FileUniqueId == fileId)).UncustomizedHomePage);
        using StreamReader text = new(Path.Combine(directory, "classicwebsummaries.csv"));
        using CsvReader csv = new(text, CultureInfo.InvariantCulture);
        Assert.Equal(
            expected == true ? 1 : 0,
            csv.GetRecords<ClassicWebSummary>().Sum(web => web.UncustomizedHomePages));
    }

    /// <summary>Observed malformed bytes take precedence over a stale allowed handler, while genuinely absent bytes permit captured-handler evidence.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Original_source_presence_controls_handler_fallback_without_losing_raw_inventory(
        bool malformedBytesPresent)
    {
        using StoreCase data = new("source-handler-priority");
        ClassicContentFixture recorded = new()
        {
            TransformInventory = inventory =>
            {
                PhysicalFileFacts file = inventory.Files!.Single(file => file.ListItemId == 1);
                file.AspxEvidenceJson = JsonSerializer.Serialize(
                    new AspxPageEvidence(
                        "Available",
                        "previous-digest",
                        "Microsoft.SharePoint.WebPartPages.WikiEditPage",
                        "Declared",
                        "previous-declaration",
                        null,
                        119,
                        false,
                        false,
                        false,
                        "Complete",
                        DateTime.UnixEpoch,
                        malformedBytesPresent ? [0xff, 0xff] : null));
                return inventory;
            },
            TransformEnrichment = input =>
            {
                if (input.DiscoveryKey == "page:1" && input.FileFacts != null)
                {
                    input.FileFacts.NoExecuteRaw = "0";
                    return input with
                    {
                        Fields = new Dictionary<string, SourceField>(input.Fields)
                        {
                            ["NoExecute"] = SourceField.Of("String", "0")
                        }
                    };
                }

                return input;
            },
        };
        (Guid assessment, ClassicReportSource source, string directory) = await RunAsync(data, recorded);
        Guid fileId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        AspxCsvRow raw = new(source.Publication.Files.Single(file => file.FileUniqueId == fileId));
        Assert.Equal("Available", raw.SourceReadState);
        Assert.Equal(malformedBytesPresent ? null : "Microsoft.SharePoint.WebPartPages.WikiEditPage", raw.PageHandler);
        if (malformedBytesPresent)
        {
            Assert.Equal("Unresolved", raw.HandlerResolution);
            Assert.NotNull(raw.SourceError);
            Assert.NotEqual("previous-digest", raw.SourceSha256);
            Assert.DoesNotContain(ReadPages(directory), page => page.FileUniqueId == fileId);
        }
        else
            Assert.Contains(ReadPages(directory), page => page.FileUniqueId == fileId);

        using StreamReader text = new(Path.Combine(directory, "aspx.csv"));
        using CsvReader csv = new(text, CultureInfo.InvariantCulture);
        await csv.ReadAsync();
        csv.ReadHeader();
        bool retained = false;
        while (await csv.ReadAsync()) retained |= csv.GetField<Guid>("FileUniqueId") == fileId;
        Assert.True(retained);
    }

    /// <summary>Supplies original ASPX bytes and native file-only setup evidence before the collection journal is sealed.</summary>
    private static WebFileInventory InventoryWithSource(WebFileInventory inventory, string key, bool fileOnly)
    {
        DiscoveryEvidence candidate = inventory.Evidence.Single(row => row.RecordKey == key);
        PhysicalFileFacts file = inventory.Files!.Single(file => file.FileUniqueId == candidate.FileUniqueId);
        file.AspxEvidenceJson = JsonSerializer.Serialize(
            new AspxPageEvidence(
                "Available",
                null,
                null,
                "PendingAnalysis",
                null,
                null,
                119,
                false,
                false,
                false,
                "Complete",
                DateTime.UnixEpoch,
                Encoding.UTF8.GetBytes("<%@ Page Inherits=\"Microsoft.SharePoint.WebPartPages.WebPartPage\" %>")));
        if (fileOnly) candidate.SetupPath = file.SetupPath = "SiteTemplates\\STS\\default.aspx";
        return inventory;
    }

    /// <summary>Builds a real captured web-part XML export with either a registered mapping or an intentionally unmapped type.</summary>
    private static ClassicPageWebPartSource Part(bool mapped)
    {
        ClassicPageWebPartSource part = ClassicContentFixture.Part("Main", 0);
        return mapped
            ? part
            : part with
            {
                ExportXml = part.ExportXml!.Replace(
                    "Microsoft.SharePoint.WebPartPages.ContentEditorWebPart",
                    "Contoso.UnmappedPart",
                    StringComparison.Ordinal)
            };
    }

    /// <summary>Runs real collection, detached analysis, immutable publication verification and report CSV output on private synthetic SQLite.</summary>
    private static async Task<(Guid Assessment, ClassicReportSource Source, string Directory)> RunAsync(
        StoreCase data,
        ClassicContentFixture recorded,
        bool usage = false)
    {
        PipelineCoordinator coordinator = new(
            data.Store,
            ClassicPageModule.Registry(recorded.OpenAsync),
            new ForbiddenOnlineEnvironment());
        PhaseReply ticket =
            await coordinator.StartPipelineAsync(new() { Collection = ClassicContentFixture.Request(usage) });
        Guid assessment = Guid.Parse(ticket.AssessmentId);
        ScanStatus status = await coordinator.WaitForCompletionAsync(assessment).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(status == ScanStatus.Finished, (await data.Store.LatestRootAsync(assessment))!.LastError);
        using ScanContext context = data.Store.CreateContext(assessment);
        ClassicReportSource source = await PipelineStore.ReadClassicReportSourceAsync(context, assessment, default) ??
                                     throw new InvalidOperationException("Expected a native publication.");
        string original = (await context.AnalysisReportRows.SingleAsync()).PayloadJson;
        string manifest = (await context.SourceSnapshots.SingleAsync()).ManifestDigest!;
        string directory = Path.Combine(data.DirectoryPath, "export");
        Directory.CreateDirectory(directory);
        await ReportManager.ExportClassicReportDataAsync(
            context,
            assessment,
            directory,
            new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "," });
        Assert.Equal(original, (await context.AnalysisReportRows.SingleAsync()).PayloadJson);
        Assert.Equal(manifest, (await context.SourceSnapshots.SingleAsync()).ManifestDigest);
        return (assessment, source, directory);
    }

    /// <summary>Reads actual emitted nullable fields rather than substituting hand-constructed Page rows.</summary>
    private static ExportedPage[] ReadPages(string directory)
    {
        using StreamReader text = new(Path.Combine(directory, "classicpages.csv"));
        using CsvReader csv = new(text, CultureInfo.InvariantCulture);
        return csv.GetRecords<ExportedPage>().ToArray();
    }

    /// <summary>Represents raw Graph event field names through the same camel-case JSON shape as actual acquired responses.</summary>
    private sealed record AuditEvent(string Operation, string ObjectId, string UserId);

    /// <summary>Reads only the CSV columns asserted by the end-to-end availability and attribution regressions.</summary>
    private sealed class ExportedPage
    {
        public Guid? FileUniqueId { get; set; }
        public Guid? ListId { get; set; }
        public int? ListItemId { get; set; }
        public int? WebPartCount { get; set; }
        public double? MappingPercentage { get; set; }
        public string MappingReadState { get; set; } = "";
        public bool? UncustomizedHomePage { get; set; }
        public int? AuditViewsCount { get; set; }
        public int? AuditUniqueUsers { get; set; }
        public string UsageQueryStatus { get; set; } = "";
        public string UsageAttributionState { get; set; } = "";
    }
}