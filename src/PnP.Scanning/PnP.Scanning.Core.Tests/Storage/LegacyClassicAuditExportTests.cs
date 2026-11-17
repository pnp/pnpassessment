#nullable enable
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using FluentAssertions;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using Xunit;

namespace PnP.Scanning.Core.Tests.Storage;

/// <summary>Exercises the historical, non-pipeline report through its CSV export boundary.</summary>
public sealed class LegacyClassicAuditExportTests : IClassFixture<ScanContextFixture>
{
    private const string Site = "https://contoso.sharepoint.com/sites/a";
    private const string PageUrl = "/sites/a/SitePages/Home.aspx";
    private const string BlogRenderer = "/sites/a/Lists/Posts/Post.aspx";
    private static readonly DateTime WindowStart = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    // Freeze the old CSV contract independently of either the EF entity or the new export DTO.
    private static readonly string[] LegacyColumns =
    [
        "PageUrl", "PageName", "PageType", "ListUrl", "ListTitle", "ListId",
        "SiteCollectionId", "WebId", "FileUniqueId", "ListItemId", "DiscoveryStatus", "AssessmentStatus",
        "ModifiedAt", "Layout", "CustomizedPageStatus", "LayoutUrl", "LayoutCustomizedPageStatus",
        "HomePage", "UncustomizedHomePage", "ModifiedBy", "WebPartCount", "MappingPercentage",
        "UnmappedWebParts", "RemediationCode", "ScanId", "SiteUrl", "WebUrl",
    ];

    private static readonly string[] AuditColumns =
    [
        "AuditViewsCount", "AuditCreatesCount", "AuditEditsCount", "AuditUniqueUsers",
        "AuditWindowStart", "AuditWindowEnd", "UsageQueryStatus", "UsageAttributionState",
    ];

    private readonly ScanContextFixture fixture;

    public LegacyClassicAuditExportTests(ScanContextFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData(19, 2, 7, 5)]
    [InlineData(0, 0, 0, 0)]
    public async Task LegacyColumnsAndValuesArePreservedAndTemplateAuditColumnsAreAppended(
        int views, int creates, int edits, int users)
    {
        Guid scan = Guid.NewGuid();
        var page = new ClassicPage
        {
            ScanId = scan,
            SiteUrl = Site,
            WebUrl = "/",
            PageUrl = PageUrl,
            PageName = "Legacy, \"Home\".aspx",
            PageType = "PublishingPage",
            ListUrl = "/sites/a/Pages",
            ListTitle = "Legacy pages",
            ListId = Guid.NewGuid(),
            SiteCollectionId = Guid.NewGuid(),
            WebId = Guid.NewGuid(),
            FileUniqueId = Guid.NewGuid(),
            ListItemId = 42,
            DiscoveryStatus = "Discovered",
            AssessmentStatus = "Complete",
            ModifiedAt = WindowStart.AddHours(3),
            Layout = "TwoColumns",
            CustomizedPageStatus = "Customized",
            LayoutUrl = "/sites/a/_catalogs/masterpage/Basic.aspx",
            LayoutCustomizedPageStatus = "Uncustomized",
            HomePage = true,
            UncustomizedHomePage = false,
            ModifiedBy = "alex@contoso.com",
            WebPartCount = 4,
            MappingPercentage = 75.5,
            UnmappedWebParts = "Contoso.One,\nContoso.Two",
            RemediationCode = "CP2",
        };
        var audit = NewAudit(scan);
        audit.AuditViewsCount = views;
        audit.AuditCreatesCount = creates;
        audit.AuditEditsCount = edits;
        audit.AuditUniqueUsers = users;
        using var context = fixture.CreateContext();
        context.AddRange(page, audit);
        await context.SaveChangesAsync();

        CsvExport export = await ExportAsync(context, scan);

        export.Header.Should().Equal(LegacyColumns.Concat(AuditColumns));
        // These are the inline names consumed by the 1.17 Pages template, not the sidecar's QueryStatus.
        export.Header.Should().Contain(new[] { "AuditViewsCount", "AuditUniqueUsers", "UsageQueryStatus" });
        export.Header.Should().NotContain(new[] { "PageHandler", "AnalysisRunId", "SnapshotId", "QueryStatus" });
        export.Rows.Should().ContainSingle();
        var row = export.Rows.Single();
        LegacyColumns.Select(column => row[column]).Should().Equal(
            PageUrl, page.PageName, "PublishingPage", page.ListUrl, "Legacy pages", page.ListId.ToString(),
            page.SiteCollectionId.ToString(), page.WebId.ToString(), page.FileUniqueId.ToString(), "42",
            "Discovered", "Complete", page.ModifiedAt.ToString(CultureInfo.InvariantCulture), "TwoColumns",
            "Customized", page.LayoutUrl, "Uncustomized", "True", "False", "alex@contoso.com", "4", "75.5",
            "Contoso.One,\nContoso.Two", "CP2", scan.ToString(), Site, "/");
        AssertCounts(row, views, creates, edits, users);
        AssertWindow(row);
        row["UsageQueryStatus"].Should().Be("succeeded");
        row["UsageAttributionState"].Should().Be("ExactContentLocator");
    }

    [Fact]
    public async Task LegacyExportSuppliesAuditColumnsDeclaredByTheEmbeddedReportTemplate()
    {
        Guid scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.ClassicPages.Add(NewPage(scan));
        await context.SaveChangesAsync();
        CsvExport export = await ExportAsync(context, scan);

        using Stream template = typeof(ReportManager).Assembly.GetManifestResourceStream(
            "PnP.Scanning.Core.Scanners.Classic.ClassicAssessmentReport.pbit")!;
        using var archive = new ZipArchive(template, ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("DataModelSchema")!.Open(), Encoding.Unicode);
        using JsonDocument schema = JsonDocument.Parse(await reader.ReadToEndAsync());
        JsonElement pages = schema.RootElement.GetProperty("model").GetProperty("tables").EnumerateArray()
            .Single(table => table.GetProperty("name").GetString() == "classicpages");
        string[] required = pages.GetProperty("columns").EnumerateArray()
            .Select(column => column.GetProperty("name").GetString()!)
            .Where(name => name.StartsWith("Audit", StringComparison.Ordinal) || name == "UsageQueryStatus")
            .ToArray();

        required.Should().NotBeEmpty();
        export.Header.Should().Contain(required);
    }

    [Theory]
    [InlineData("partial", "partial", true)]
    [InlineData("failed", "failed", false)]
    [InlineData("skipped", "skipped", false)]
    [InlineData("unexpected", "notcollected", false)]
    [InlineData(null, "notcollected", false)]
    public async Task ExactAuditStatusControlsWhetherCountersAreKnown(
        string? storedStatus, string expectedStatus, bool known)
    {
        Guid scan = Guid.NewGuid();
        var audit = NewAudit(scan);
        audit.QueryStatus = storedStatus!;
        using var context = fixture.CreateContext();
        context.AddRange(NewPage(scan), audit);
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        AssertCounts(row, known ? 11 : null, known ? 2 : null, known ? 3 : null, known ? 4 : null);
        row["UsageQueryStatus"].Should().Be(expectedStatus);
        row["UsageAttributionState"].Should().Be(known ? "ExactContentLocator" : "NotAcquired");
        if (storedStatus is "partial" or "failed" or "skipped") AssertWindow(row);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SavedSkipUsageOptionOverridesStaleSuccessfulAuditOnlyWhenTrue(bool skip)
    {
        Guid scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.AddRange(NewPage(scan), NewAudit(scan), new Property
        {
            ScanId = scan,
            Name = "skipusageinformation",
            Type = "bool",
            Value = skip.ToString(),
        });
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        if (skip)
        {
            AssertUnavailable(row, "skipped");
        }
        else
        {
            AssertCounts(row, 11, 2, 3, 4);
            row["UsageQueryStatus"].Should().Be("succeeded");
        }
    }

    [Fact]
    public async Task NoAuditDataLeavesCountsAndWindowBlankRatherThanInventingZero()
    {
        Guid scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.ClassicPages.Add(NewPage(scan));
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        AssertUnavailable(row);
        row["AuditWindowStart"].Should().BeEmpty();
        row["AuditWindowEnd"].Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SamePageUrlInAnotherScanOrSiteCannotSupplyUsage(bool differentScan)
    {
        Guid scan = Guid.NewGuid();
        var audit = NewAudit(differentScan ? Guid.NewGuid() : scan);
        if (!differentScan) audit.SiteUrl = "https://contoso.sharepoint.com/sites/b";
        using var context = fixture.CreateContext();
        context.AddRange(NewPage(scan), audit);
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        AssertUnavailable(row);
    }

    [Fact]
    public async Task FullQueryLocatorKeepsDifferentBlogItemsAndRendererUsageSeparate()
    {
        Guid scan = Guid.NewGuid();
        string first = BlogRenderer + "?ID=7";
        string second = BlogRenderer + "?ID=8";
        var secondAudit = NewAudit(scan, second);
        secondAudit.AuditViewsCount = 23;
        using var context = fixture.CreateContext();
        context.AddRange(
            NewPage(scan, first, "BlogPage", 7), NewPage(scan, second, "BlogPage", 8),
            NewAudit(scan, first), secondAudit, NewAudit(scan, BlogRenderer));
        await context.SaveChangesAsync();

        CsvExport export = await ExportAsync(context, scan);

        export.Rows.Should().HaveCount(2);
        var firstRow = export.Rows.Single(row => row["PageUrl"] == first);
        var secondRow = export.Rows.Single(row => row["PageUrl"] == second);
        AssertCounts(firstRow, 11, 2, 3, 4);
        AssertCounts(secondRow, 23, 2, 3, 4);
        firstRow["UsageAttributionState"].Should().Be("ExactContentLocator");
        secondRow["UsageAttributionState"].Should().Be("ExactContentLocator");
    }

    [Theory]
    [InlineData("", 7, false)]
    [InlineData("", null, false)]
    [InlineData("?ID=8", 7, false)]
    [InlineData("?ID=0", 0, false)]
    [InlineData("?ID=-1", -1, false)]
    [InlineData("?Other=7", 7, false)]
    [InlineData("?ID=7", 7, true)]
    [InlineData("?ID=7", null, true)]
    [InlineData("?ID=7&other=1", 7, true)]
    [InlineData("?other=1&ID=7", 7, true)]
    [InlineData("?ID=7#section", 7, true)]
    [InlineData("?ID=7&other=1#section&ID=8", 7, true)]
    [InlineData("#section?ID=7", 7, false)]
    [InlineData("?other=1#section&ID=7", 7, false)]
    [InlineData("?ID=7&ID=8", 7, false)]
    [InlineData("?ID=8&ID=7", 7, false)]
    [InlineData("?ID=7&ID=7", 7, false)]
    [InlineData("?ID=7&ID=invalid", 7, false)]
    [InlineData("?ID=7&ID", 7, false)]
    [InlineData("?ID&ID=7", 7, false)]
    public async Task BlogUsageRequiresAnObservedPositiveItemIdMatchingStoredItemWhenKnown(
        string query, int? itemId, bool exactItem)
    {
        Guid scan = Guid.NewGuid();
        string url = BlogRenderer + query;
        using var context = fixture.CreateContext();
        context.AddRange(NewPage(scan, url, "BlogPage", itemId), NewAudit(scan, url));
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        AssertCounts(row, exactItem ? 11 : null, exactItem ? 2 : null,
            exactItem ? 3 : null, exactItem ? 4 : null);
        row["UsageAttributionState"].Should().Be(exactItem ? "ExactContentLocator" : "ItemLocatorNotObserved");
    }

    [Fact]
    public async Task SharedPageAddressAcrossWebsDoesNotMultiplyOrBorrowAuditRows()
    {
        Guid scan = Guid.NewGuid();
        var secondPage = NewPage(scan);
        secondPage.WebUrl = "/sub";
        using var context = fixture.CreateContext();
        context.AddRange(NewPage(scan), secondPage, NewAudit(scan));
        await context.SaveChangesAsync();

        CsvExport export = await ExportAsync(context, scan);

        export.Rows.Should().HaveCount(2);
        export.Rows.Select(row => row["WebUrl"]).Should().BeEquivalentTo(new[] { "/", "/sub" });
        foreach (var row in export.Rows) AssertUnavailable(row);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("skipped")]
    [InlineData("partial")]
    public async Task UniqueSiteCoveragePropagatesStatusAndWindowButNeverItsCounters(string status)
    {
        Guid scan = Guid.NewGuid();
        var sentinel = NewAudit(scan, "/sites/a");
        sentinel.QueryStatus = status;
        using var context = fixture.CreateContext();
        context.AddRange(NewPage(scan), sentinel);
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        AssertUnavailable(row, status);
        AssertWindow(row);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MultipleSiteSummaryLocatorsCannotSupplyCoverage(int summaries)
    {
        Guid scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.ClassicPages.Add(NewPage(scan));
        for (int index = 0; index < summaries; index++)
        {
            var sentinel = NewAudit(scan, "/sites/a" + new string('/', index));
            SetCountsToZero(sentinel);
            context.ClassicPageAuditUsages.Add(sentinel);
        }
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        AssertUnavailable(row);
        row["AuditWindowStart"].Should().BeEmpty();
        row["AuditWindowEnd"].Should().BeEmpty();
    }

    [Theory]
    [InlineData(Site, "/sites/a", PageUrl)]
    [InlineData("https://contoso.sharepoint.com", "/", "/SitePages/Home.aspx")]
    public async Task SuccessfulEmptySiteWithValidWindowProvesZeroForUnmatchedFile(
        string siteUrl, string sentinelUrl, string fileUrl)
    {
        Guid scan = Guid.NewGuid();
        var page = NewPage(scan, fileUrl);
        page.SiteUrl = siteUrl;
        var sentinel = NewAudit(scan, sentinelUrl);
        sentinel.SiteUrl = siteUrl;
        SetCountsToZero(sentinel);
        using var context = fixture.CreateContext();
        context.AddRange(page, sentinel);
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        AssertCounts(row, 0, 0, 0, 0);
        AssertWindow(row);
        row["UsageQueryStatus"].Should().Be("succeeded");
        row["UsageAttributionState"].Should().Be("CoveredFileNoEvents");
    }

    [Theory]
    [InlineData("nonzero-views")]
    [InlineData("nonzero-creates")]
    [InlineData("nonzero-edits")]
    [InlineData("nonzero-users")]
    [InlineData("default-start")]
    [InlineData("default-end")]
    [InlineData("reversed-window")]
    public async Task SuccessfulSentinelWithoutEmptyQueryProofCannotInventZero(string invalidProof)
    {
        Guid scan = Guid.NewGuid();
        var sentinel = NewAudit(scan, "/sites/a");
        SetCountsToZero(sentinel);
        switch (invalidProof)
        {
            case "nonzero-views": sentinel.AuditViewsCount = 1; break;
            case "nonzero-creates": sentinel.AuditCreatesCount = 1; break;
            case "nonzero-edits": sentinel.AuditEditsCount = 1; break;
            case "nonzero-users": sentinel.AuditUniqueUsers = 1; break;
            case "default-start": sentinel.AuditWindowStart = default; break;
            case "default-end": sentinel.AuditWindowEnd = default; break;
            case "reversed-window": sentinel.AuditWindowEnd = WindowStart.AddDays(-1); break;
        }
        using var context = fixture.CreateContext();
        context.AddRange(NewPage(scan), sentinel);
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        AssertCounts(row, null, null, null, null);
        row["UsageAttributionState"].Should().NotBe("ExactContentLocator").And.NotBe("CoveredFileNoEvents");
    }

    [Fact]
    public async Task AnotherPagesSuccessfulAuditIsNotSiteCoverageForAnUnmatchedFile()
    {
        Guid scan = Guid.NewGuid();
        using var context = fixture.CreateContext();
        context.AddRange(NewPage(scan), NewAudit(scan, "/sites/a/SitePages/Other.aspx"));
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        AssertUnavailable(row);
    }

    [Fact]
    public async Task ExactPageAuditTakesPrecedenceOverSiteCoverage()
    {
        Guid scan = Guid.NewGuid();
        var sentinel = NewAudit(scan, "/sites/a");
        sentinel.QueryStatus = "failed";
        using var context = fixture.CreateContext();
        context.AddRange(NewPage(scan), NewAudit(scan), sentinel);
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        AssertCounts(row, 11, 2, 3, 4);
        row["UsageQueryStatus"].Should().Be("succeeded");
        row["UsageAttributionState"].Should().Be("ExactContentLocator");
    }

    [Theory]
    [InlineData("")]
    [InlineData("?ID=7")]
    public async Task EmptySiteCoverageCannotAttributeZeroToUnobservedBlogItem(string query)
    {
        Guid scan = Guid.NewGuid();
        var sentinel = NewAudit(scan, "/sites/a");
        SetCountsToZero(sentinel);
        using var context = fixture.CreateContext();
        context.AddRange(NewPage(scan, BlogRenderer + query, "BlogPage", 7), sentinel);
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        AssertCounts(row, null, null, null, null);
        row["UsageAttributionState"].Should().Be("ItemLocatorNotObserved");
    }

    [Fact]
    public async Task QueryAddressedFileWithoutExactAuditCannotInheritEmptySiteZeroes()
    {
        Guid scan = Guid.NewGuid();
        string url = PageUrl + "?view=archive";
        var sentinel = NewAudit(scan, "/sites/a");
        SetCountsToZero(sentinel);
        using var context = fixture.CreateContext();
        context.AddRange(NewPage(scan, url), sentinel);
        await context.SaveChangesAsync();

        var row = (await ExportAsync(context, scan)).Rows.Single();

        row["PageUrl"].Should().Be(url);
        AssertCounts(row, null, null, null, null);
        row["UsageAttributionState"].Should().NotBe("CoveredFileNoEvents").And.NotBe("ExactContentLocator");
    }

    private static ClassicPage NewPage(
        Guid scan, string url = PageUrl, string type = "WikiPage", int? itemId = null) => new()
    {
        ScanId = scan,
        SiteUrl = Site,
        WebUrl = "/",
        PageUrl = url,
        PageName = "Home.aspx",
        PageType = type,
        ListItemId = itemId,
    };

    private static ClassicPageAuditUsage NewAudit(Guid scan, string url = PageUrl) => new()
    {
        ScanId = scan,
        SiteUrl = Site,
        WebUrl = "/",
        PageUrl = url,
        AuditViewsCount = 11,
        AuditCreatesCount = 2,
        AuditEditsCount = 3,
        AuditUniqueUsers = 4,
        AuditWindowStart = WindowStart,
        AuditWindowEnd = WindowEnd,
        QueryStatus = "succeeded",
    };

    private static void SetCountsToZero(ClassicPageAuditUsage audit)
    {
        audit.AuditViewsCount = 0;
        audit.AuditCreatesCount = 0;
        audit.AuditEditsCount = 0;
        audit.AuditUniqueUsers = 0;
    }

    private static void AssertCounts(
        IReadOnlyDictionary<string, string> row, int? views, int? creates, int? edits, int? users)
    {
        row["AuditViewsCount"].Should().Be(views?.ToString(CultureInfo.InvariantCulture) ?? "");
        row["AuditCreatesCount"].Should().Be(creates?.ToString(CultureInfo.InvariantCulture) ?? "");
        row["AuditEditsCount"].Should().Be(edits?.ToString(CultureInfo.InvariantCulture) ?? "");
        row["AuditUniqueUsers"].Should().Be(users?.ToString(CultureInfo.InvariantCulture) ?? "");
    }

    private static void AssertUnavailable(
        IReadOnlyDictionary<string, string> row, string status = "notcollected")
    {
        AssertCounts(row, null, null, null, null);
        row["UsageQueryStatus"].Should().Be(status);
        row["UsageAttributionState"].Should().Be("NotAcquired");
    }

    private static void AssertWindow(IReadOnlyDictionary<string, string> row)
    {
        DateTime.Parse(row["AuditWindowStart"], CultureInfo.InvariantCulture).Should().Be(WindowStart);
        DateTime.Parse(row["AuditWindowEnd"], CultureInfo.InvariantCulture).Should().Be(WindowEnd);
    }

    private static async Task<CsvExport> ExportAsync(ScanContext context, Guid scan)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pnp-legacy-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "," };
            await ReportManager.ExportClassicReportDataAsync(context, scan, directory, configuration);
            using var reader = new StreamReader(Path.Combine(directory, "classicpages.csv"));
            using var csv = new CsvReader(reader, configuration);
            csv.Read().Should().BeTrue();
            csv.ReadHeader();
            string[] header = csv.HeaderRecord!;
            var rows = new List<Dictionary<string, string>>();
            while (csv.Read())
            {
                // Read raw fields rather than a numeric DTO: an empty field must not silently become zero.
                rows.Add(header.ToDictionary(column => column, column => csv.GetField(column) ?? ""));
            }
            return new CsvExport(header, rows);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record CsvExport(string[] Header, List<Dictionary<string, string>> Rows);
}
