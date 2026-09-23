using FluentAssertions;
using CsvHelper;
using CsvHelper.Configuration;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Globalization;
using System.Net;
using System.Text;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class AspxAcquisitionEvidenceTests : IClassFixture<ScanContextFixture>
{
    private readonly ScanContextFixture database;

    public AspxAcquisitionEvidenceTests(ScanContextFixture database) => this.database = database;

    [Fact]
    public void Direct_rest_requests_use_the_required_test_traffic_user_agent()
    {
        using var request = PnPContextSharePointAspxRestClient.CreateGetRequest(
            new Uri("https://contoso.sharepoint.com/_api/web"));

        request.Headers.UserAgent.ToString().Should().Be("testtraffic-smr");
    }

    [Fact]
    public void Pagination_integrity_fails_closed()
    {
        var wrongOrdinal = AspxPaginationContract.Validate(new[]
        {
            Page(0, null, "token-1", terminal: false),
            Page(2, "token-1", null, terminal: true),
        }, DiscoveryTerminalOutcome.Complete);
        var repeatedOrdinal = AspxPaginationContract.Validate(new[]
        {
            Page(0, null, "token-1", terminal: false),
            Page(0, "token-1", null, terminal: true),
        }, DiscoveryTerminalOutcome.Complete);
        var terminalWithToken = AspxPaginationContract.Validate(
            new[] { Page(0, null, "token-1", terminal: true) }, DiscoveryTerminalOutcome.Complete);
        var missingTerminal = AspxPaginationContract.Validate(
            new[] { Page(0, null, "token-1", terminal: false) }, DiscoveryTerminalOutcome.Complete);
        var denied = AspxPaginationContract.Validate(
            new[] { Page(0, null, null, terminal: true) }, DiscoveryTerminalOutcome.Denied);

        new[] { wrongOrdinal, repeatedOrdinal, terminalWithToken, missingTerminal }
            .Should().OnlyContain(result => result.Outcome == DiscoveryTerminalOutcome.Unknown);
        missingTerminal.OutstandingTokenCount.Should().Be(1);
        denied.Outcome.Should().Be(DiscoveryTerminalOutcome.Denied);
    }

    [Fact]
    public void Pagination_evidence_hashes_continuation_values()
    {
        const string rawToken = "Paged=TRUE&p_ID=42";
        var nextLink = "https://contoso.sharepoint.com/_api/web/lists?$select=Id&$skiptoken=" +
            Uri.EscapeDataString(rawToken);
        var request = new Uri(nextLink);
        var page = SharePointRestResponseParser.Parse(request, HttpStatusCode.OK,
            Encoding.UTF8.GetBytes("{\"value\":[]}"), "application/json", "request", "correlation",
            DateTimeOffset.Parse("2026-09-12T12:00:00Z"));

        var durableEndpoint = AspxDurableRequestEvidence.Endpoint(request);
        var durableEvidence = AspxDurableRequestEvidence.Reference(request, page, "Id");
        durableEndpoint.Should().Contain("$skiptoken=sha256").And.NotContain("Paged");
        durableEvidence.Should().Contain("$skiptoken=sha256").And.NotContain("Paged");

        var receipts = new[]
        {
            Page(0, null, nextLink, terminal: false),
            Page(1, nextLink, null, terminal: true) with { ActualEndpoint = durableEndpoint },
        };
        AspxPaginationContract.Validate(receipts, DiscoveryTerminalOutcome.Complete).GapCodes.Should().BeEmpty();
        receipts[0].NextTokenHash.Should().Be(receipts[1].RequestTokenHash).And.HaveLength(64);
    }

    [Fact]
    public void Semantic_denial_is_terminal_and_has_no_items()
    {
        var uri = new Uri("https://contoso.sharepoint.com/sites/a/_api/web/webs?$select=Id");
        var login = SharePointRestResponseParser.Parse(uri, HttpStatusCode.OK,
            Encoding.UTF8.GetBytes("<!DOCTYPE html><html><form id=\"loginForm\" action=\"https://login.microsoftonline.com/\">Sign in to your account</form></html>"),
            "text/html", "request-1", "correlation-1", DateTimeOffset.Parse("2026-09-12T12:00:00Z"));
        var denied = SharePointRestResponseParser.Parse(uri, HttpStatusCode.OK,
            Encoding.UTF8.GetBytes("{\"error\":{\"code\":\"-2147024891, System.UnauthorizedAccessException\",\"message\":{\"value\":\"Access denied.\"}}}"),
            "application/json", "request-2", "correlation-2", DateTimeOffset.Parse("2026-09-12T12:00:01Z"));

        login.Outcome.Should().Be(DiscoveryTerminalOutcome.Denied);
        login.SemanticDetectorResult.Should().Be(SharePointSemanticDetectorResults.LoginShell);
        login.Items.Should().BeEmpty();
        denied.Outcome.Should().Be(DiscoveryTerminalOutcome.Denied);
        denied.SemanticDetectorResult.Should().Be(SharePointSemanticDetectorResults.AccessDenied);
        denied.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void Transport_denial_remains_terminal(HttpStatusCode status)
    {
        var page = SharePointRestResponseParser.Parse(new Uri("https://contoso.sharepoint.com/_api/web"),
            status, Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"Access denied\"}}"),
            "application/json");

        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Denied);
        page.Items.Should().BeEmpty();
        page.ErrorCode.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(1, "Applicable", true)]
    [InlineData(0, "SystemOrVirtualOnly", false)]
    [InlineData(2, "NotApplicable", false)]
    public void Actual_list_base_type_controls_raw_file_admission(
        int baseType, string applicability, bool rawRequired)
    {
        var decision = AspxListApplicabilityPolicy.Evaluate(baseType);

        decision.Applicability.ToString().Should().Be(applicability);
        decision.RawLibraryRequired.Should().Be(rawRequired);
        decision.FormsAndViewsRequired.Should().BeTrue();
    }

    [Fact]
    public async Task Acquisition_evidence_is_persisted_in_the_assessment_database()
    {
        var scanId = Guid.NewGuid();
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        using var provider = new EvidenceProvider();

        await new AssessmentWebDiscovery(scanId, "https://contoso.sharepoint.com/sites/a", "/", writer)
            .RunAsync(provider, CancellationToken.None);

        using var read = database.CreateContext();
        var rows = read.ClassicPageDiscoveries.Where(row => row.ScanId == scanId).ToArray();
        rows.Should().Contain(row => row.RowType == "Scope" && row.ScopeType == "Surface" &&
            !string.IsNullOrWhiteSpace(row.EvidenceJson));
        rows.Should().Contain(row => row.RowType == "Reference" &&
            row.DiscoveryStatus == "Discovered" && !string.IsNullOrWhiteSpace(row.EvidenceJson));
        rows.Should().Contain(row => row.RowType == "Pagination" &&
            row.DiscoveryStatus == "Complete" && !string.IsNullOrWhiteSpace(row.EvidenceJson));
        rows.Should().Contain(row => row.RowType == "Gap" &&
            row.ErrorCodes == "fixture:coverage_gap");

        var report = Path.Combine(Path.GetTempPath(), "aspx-evidence-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(report);
        try
        {
            using var export = database.CreateContext();
            await ReportManager.ExportClassicReportDataAsync(export, scanId, report,
                new CsvConfiguration(CultureInfo.InvariantCulture));
            using var csv = new CsvReader(new StreamReader(Path.Combine(report, "discovery.csv")),
                CultureInfo.InvariantCulture);
            var exported = csv.GetRecords<ClassicPageDiscovery>().ToArray();
            exported.Should().Contain(row => row.RowType == "Reference" &&
                !string.IsNullOrWhiteSpace(row.EvidenceJson));
            exported.Should().Contain(row => row.RowType == "Pagination" &&
                !string.IsNullOrWhiteSpace(row.EvidenceJson));
        }
        finally
        {
            Directory.Delete(report, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_still_persists_evidence_already_collected_by_the_web_worker()
    {
        var scanId = Guid.NewGuid();
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        using var provider = new EvidenceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => new AssessmentWebDiscovery(scanId,
            "https://contoso.sharepoint.com/sites/a", "/", writer)
            .RunAsync(provider, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();

        using var read = database.CreateContext();
        read.ClassicPageDiscoveries.Any(row => row.ScanId == scanId &&
            row.RowType == "Reference" && row.EvidenceJson != null).Should().BeTrue();
    }

    [Fact]
    public async Task Existing_scan_scope_produces_the_terminal_coverage_verdict()
    {
        var completeScan = Guid.NewGuid();
        var declaredScan = Guid.NewGuid();
        var homePageScan = Guid.NewGuid();
        var incompleteScan = Guid.NewGuid();
        var missingSelectionScan = Guid.NewGuid();
        var unrecognizedSelectionScan = Guid.NewGuid();
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var pageSelection = Scope(homePageScan, "assessment:page-selection", "PageSelection", "Complete");
        pageSelection.ObservationMethod = "HomePageOnly";
        await writer.WriteAsync(new[]
        {
            Scope(completeScan, "assessment:site-selection", "Tenant", "Complete"),
            Scope(completeScan, "web:root", "Web", "Complete"),
            Scope(declaredScan, "assessment:site-selection", "SiteSelection", "Complete"),
            Scope(declaredScan, "web:root", "Web", "Complete"),
            Scope(homePageScan, "assessment:site-selection", "Tenant", "Complete"),
            pageSelection,
            Scope(homePageScan, "web:root", "Web", "Complete"),
            Scope(incompleteScan, "assessment:site-selection", "SiteSelection", "Complete"),
            Scope(incompleteScan, "web:root", "Web", "Denied"),
            Scope(missingSelectionScan, "web:root", "Web", "Complete"),
            Scope(unrecognizedSelectionScan, "assessment:site-selection", "AuthorizedSurface", "Complete"),
            Scope(unrecognizedSelectionScan, "web:root", "Web", "Complete"),
        });

        (await writer.FinalizeScanAsync(completeScan)).Should().Be(DiscoveryVerdict.CompleteTenantVerified);
        (await writer.FinalizeScanAsync(declaredScan)).Should().Be(DiscoveryVerdict.CompleteDeclaredSubset);
        (await writer.FinalizeScanAsync(homePageScan)).Should().Be(DiscoveryVerdict.CompleteDeclaredSubset);
        (await writer.FinalizeScanAsync(incompleteScan)).Should().Be(DiscoveryVerdict.Incomplete);
        (await writer.FinalizeScanAsync(missingSelectionScan)).Should().Be(DiscoveryVerdict.Unknown);
        (await writer.FinalizeScanAsync(unrecognizedSelectionScan)).Should().Be(DiscoveryVerdict.Unknown);

        using var read = database.CreateContext();
        read.ClassicPageDiscoveries.Single(row =>
            row.ScanId == completeScan && row.RecordKey == "summary:coverage")
            .DiscoveryStatus.Should().Be(nameof(DiscoveryVerdict.CompleteTenantVerified));
        read.ClassicPageDiscoveries.Single(row =>
            row.ScanId == incompleteScan && row.RecordKey == "summary:coverage")
            .DiscoveryStatus.Should().Be(nameof(DiscoveryVerdict.Incomplete));
        var homePageSummary = read.ClassicPageDiscoveries.Single(row =>
            row.ScanId == homePageScan && row.RecordKey == "summary:coverage");
        homePageSummary.ObservationMethod.Should().Be("HomePageOnly");
        homePageSummary.EvidenceJson.Should().Contain("\"pageScope\":\"HomePageOnly\"");
    }

    [Fact]
    public async Task Evidence_replay_preserves_failures_and_marks_conflicts_unknown()
    {
        var scanId = Guid.NewGuid();
        var writer = new AssessmentDiscoveryWriter(database.CreateContext);
        var referenceA = Guid.NewGuid();
        var referenceB = Guid.NewGuid();
        await writer.WriteAsync(new[]
        {
            Evidence(scanId, "surface:one", "Scope", "Surface", "Denied", "surface-a"),
            Evidence(scanId, "reference:one", "Reference", "ListFormReference", "Discovered", "reference-a",
                referenceA),
            Evidence(scanId, "pagination:one", "Pagination", "RequestPage", "Complete", "pagination-a"),
        });
        await writer.WriteAsync(new[]
        {
            Evidence(scanId, "surface:one", "Scope", "Surface", "Complete", "surface-b"),
            Evidence(scanId, "reference:one", "Reference", "ListFormReference", "Discovered", "reference-b",
                referenceB),
            Evidence(scanId, "pagination:one", "Pagination", "RequestPage", "Complete", "pagination-b"),
        });

        using var read = database.CreateContext();
        read.ClassicPageDiscoveries.Single(row => row.ScanId == scanId && row.RecordKey == "surface:one")
            .DiscoveryStatus.Should().Be("Denied");
        var reference = read.ClassicPageDiscoveries.Single(row =>
            row.ScanId == scanId && row.RecordKey == "reference:one");
        reference.DiscoveryStatus.Should().Be("Unknown");
        reference.ErrorCodes.Should().Contain(DiscoveryGapCodes.MetadataConflict);
        var pagination = read.ClassicPageDiscoveries.Single(row =>
            row.ScanId == scanId && row.RecordKey == "pagination:one");
        pagination.DiscoveryStatus.Should().Be("Unknown");
        pagination.ErrorCodes.Should().Contain(DiscoveryGapCodes.ChangedDuringScan);
    }

    private static ClassicPageDiscovery Scope(Guid scanId, string key, string type, string status) => new()
    {
        ScanId = scanId,
        RecordKey = key,
        RowType = "Scope",
        ScopeType = type,
        DiscoveryStatus = status,
        ObservedAtUtc = DateTime.UtcNow,
    };

    private static ClassicPageDiscovery Evidence(Guid scanId, string key, string rowType, string scopeType,
        string status, string evidence, Guid? fileId = null) => new()
    {
        ScanId = scanId,
        RecordKey = key,
        RowType = rowType,
        ScopeType = scopeType,
        DiscoveryStatus = status,
        FileUniqueId = fileId,
        EvidenceJson = evidence,
        ObservedAtUtc = DateTime.UtcNow,
    };

    private static AspxPaginationPageReceipt Page(int ordinal, string request, string next, bool terminal) => new(
        AspxAcquisitionVersions.PaginationReceipt, "scope", "authority/v1", new string('a', 64), "GET",
        "https://contoso.sharepoint.com/_api/web/lists", "Id", string.Empty, ordinal,
        AspxPaginationContract.TokenHash(request), 1, AspxPaginationContract.TokenHash(next), new string('b', 64),
        200, SharePointSemanticDetectorResults.None, 1, 1, "request", "correlation", null, terminal,
        DateTimeOffset.Parse("2026-09-12T12:00:00Z"));

    private sealed class EvidenceProvider : IAspxDiscoveryProvider, IAspxReferenceAcquisitionProvider
    {
        internal EvidenceProvider()
        {
            var receipt = Page(0, null, null, terminal: true);
            ReferenceCollector.AddSurface(new AspxSurfaceDenominatorRow(
                "web", null, "surface:web", AspxSurfaceApplicability.Applicable,
                null, null, null, null, null, AspxRuntimeCounterexampleState.NoneObserved,
                "Fixture", "https://contoso.sharepoint.com/sites/a", "authority/v1", new string('a', 64),
                "GET", "https://contoso.sharepoint.com/sites/a/_api/web", "Id", string.Empty,
                "declared", "fixture", 1, AspxExpectedCountState.Known, 1,
                DiscoveryTerminalOutcome.Complete, "satisfied", false, new string('b', 64), 0,
                null, null, DateTimeOffset.UtcNow, new[] { "fixture:evidence" },
                "FixtureAdapter", "physical"), new[] { receipt });
            ReferenceCollector.AddReference(new AspxReferenceCandidate(
                AspxReferenceSourceKinds.ListForm, "form-1", "Fixture", null,
                "/sites/a/Lists/X/DispForm.aspx", "/sites/a/lists/x/dispform.aspx", null,
                AspxReferenceDispositions.LinkedPhysicalCustomized, null,
                "cccccccc-cccc-cccc-cccc-cccccccccccc", "customized", "fixture",
                new[] { "fixture:reference" }));
            ReferenceCollector.AddGap("fixture:coverage_gap");
        }

        public DiscoveryScopeRegistration RootScope { get; } = new(
            "web", null, DiscoveryScopeKind.Web, null,
            "https://contoso.sharepoint.com/sites/a", "fixture");

        public AspxReferenceCollector ReferenceCollector { get; } = new();

        public Task<DiscoveryChildEnumerationResult> EnumerateChildrenAsync(
            DiscoveryScopeRegistration parent, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiscoveryChildEnumerationResult(
                AspxDiscoveryHierarchy.ChildKindFor(parent.Kind),
                Array.Empty<DiscoveryChildExpectation>(),
                Array.Empty<DiscoveryScopeRegistration>(),
                DiscoveryTerminalOutcome.Complete,
                parent.PermissionContext));

        public IRawDiscoverySource CreateRawSource(DiscoveryScopeRegistration surface) => null;

        public void Dispose() { }
    }
}
