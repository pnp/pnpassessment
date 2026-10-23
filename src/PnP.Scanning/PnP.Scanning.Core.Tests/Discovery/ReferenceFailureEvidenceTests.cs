using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Storage;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Net;
using System.Text.Json;
using Xunit;
using OfflineFileSdk = PnP.Scanning.Core.Tests.Discovery.ReferenceFileResolutionTests.OfflineFileSdk;
using FileTransport = PnP.Scanning.Core.Tests.Discovery.ReferenceFileResolutionTests.FileTransport;

namespace PnP.Scanning.Core.Tests.Discovery;

[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class ReferenceFailureEvidenceTests : IClassFixture<ScanContextFixture>
{
    private readonly ScanContextFixture database;
    public ReferenceFailureEvidenceTests(ScanContextFixture database) => this.database = database;

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, DiscoveryTerminalOutcome.Denied, "locator_denied")]
    [InlineData(HttpStatusCode.InternalServerError, DiscoveryTerminalOutcome.Failed, "locator_resolution_failed")]
    [InlineData(HttpStatusCode.BadRequest, DiscoveryTerminalOutcome.Failed, "locator_resolution_failed")]
    public async Task Real_http_failure_keeps_request_stage_original_type_message_and_http_diagnostics(
        HttpStatusCode status, DiscoveryTerminalOutcome expected, string code)
    {
        using var sdk = await OfflineFileSdk.CreateAsync();
        SetFailure(sdk, status);

        var result = await sdk.Client.ResolveFileAsync(OfflineFileSdk.FilePath);

        result.Outcome.Should().Be(expected);
        result.ErrorCode.Should().Be(code);
        result.ErrorStage.Should().Be("FileRequest");
        result.ErrorDetail.Should().Contain("SharePointRestServiceException")
            .And.Contain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.Contain("Offline physical-file request failed")
            .And.Contain("Offline.FileFailure")
            .And.Contain(FileTransport.RequestId);
        result.ErrorDetail.Should().NotContain(" at PnP.").And.NotContain("\r").And.NotContain("\n");
        result.FileUniqueId.Should().BeNull();
        result.ServerRelativeUrl.Should().Be(OfflineFileSdk.FilePath);
        sdk.Transport.FileRequests.Should().ContainSingle();
        sdk.Transport.ReturnedFileStatuses.Should().Equal(status);
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Http_200_missing_required_file_name_is_a_projection_failure_not_a_request_failure()
    {
        using var sdk = await OfflineFileSdk.CreateAsync();
        sdk.Transport.FileJson = ReferenceFileResolutionTests.FilePayload("item", omitName: true);

        var result = await sdk.Client.ResolveFileAsync(OfflineFileSdk.FilePath);

        result.Outcome.Should().Be(DiscoveryTerminalOutcome.Failed);
        result.ErrorCode.Should().Be("locator_resolution_failed");
        result.ErrorStage.Should().Be("FileMetadataProjection");
        result.ErrorDetail.Should().Contain("ClientException").And.Contain("Name");
        sdk.Transport.ReturnedFileStatuses.Should().Equal(HttpStatusCode.OK);
        sdk.Transport.FileRequests.Should().ContainSingle();
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Actual_sdk_not_found_result_has_request_stage_and_does_not_invent_a_physical_identity()
    {
        using var sdk = await OfflineFileSdk.CreateAsync();
        sdk.Transport.FileStatus = HttpStatusCode.NotFound;
        sdk.Transport.FileJson = ErrorPayload("The offline file does not exist.",
            "-2130575338, System.IO.FileNotFoundException");

        var result = await sdk.Client.ResolveFileAsync(OfflineFileSdk.FilePath);

        result.Outcome.Should().Be(DiscoveryTerminalOutcome.Failed);
        result.ErrorCode.Should().Be("locator_not_found");
        result.ErrorStage.Should().Be("FileRequest");
        result.ErrorDetail.Should().NotBeNullOrWhiteSpace();
        result.FileUniqueId.Should().BeNull();
        sdk.Transport.FileRequests.Should().ContainSingle();
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Theory]
    [InlineData("denied", "FileRequest", "SharePointRestServiceException")]
    [InlineData("server", "FileRequest", "SharePointRestServiceException")]
    [InlineData("projection", "FileMetadataProjection", "ClientException")]
    public async Task Actual_provider_persists_safe_diagnostics_in_reference_columns_and_evidence_json(
        string failure, string stage, string errorType)
    {
        using var sdk = await OfflineFileSdk.CreateAsync();
        if (failure == "projection") sdk.Transport.FileJson = ReferenceFileResolutionTests.FilePayload("item", omitName: true);
        else SetFailure(sdk, failure == "denied" ? HttpStatusCode.Forbidden : HttpStatusCode.InternalServerError);
        var scan = Guid.NewGuid();
        using var provider = Provider(sdk);

        await new AssessmentWebDiscovery(scan, OfflineFileSdk.WebUrl, "/",
            new AssessmentDiscoveryWriter(database.CreateContext)).RunAsync(provider, CancellationToken.None);

        var candidate = provider.ReferenceCollector.ReadReferenceEvidence().Should().ContainSingle().Which;
        candidate.Disposition.Should().Be(AspxReferenceDispositions.ReferenceUnavailable);
        candidate.ErrorStage.Should().Be(stage);
        candidate.ErrorDetail.Should().Contain(errorType);
        using var db = database.CreateContext();
        var reference = await db.ClassicPageDiscoveries.SingleAsync(row => row.ScanId == scan && row.RowType == "Reference");
        reference.DiscoveryStatus.Should().Be("Failed");
        reference.ErrorStage.Should().Be(stage);
        reference.ErrorDetail.Should().Be(candidate.ErrorDetail);
        reference.ErrorCodes.Should().Be(failure == "denied" ? "locator_denied" : "locator_resolution_failed");
        using var evidence = JsonDocument.Parse(reference.EvidenceJson);
        evidence.RootElement.GetProperty("errorStage").GetString().Should().Be(stage);
        evidence.RootElement.GetProperty("errorDetail").GetString().Should().Be(candidate.ErrorDetail);
        (await db.ClassicPageDiscoveries.CountAsync(row => row.ScanId == scan && row.RowType == "Page"))
            .Should().Be(0, "an actual failed resolution remains evidence, not an invented physical page");
        sdk.Transport.FileRequests.Should().ContainSingle();
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Successful_file_without_item_metadata_reaches_page_and_linked_reference_persistence()
    {
        using var sdk = await OfflineFileSdk.CreateAsync();
        sdk.Transport.FileJson = ReferenceFileResolutionTests.FilePayload("omitted", "missing");
        var scan = Guid.NewGuid();
        using var provider = Provider(sdk);

        await new AssessmentWebDiscovery(scan, OfflineFileSdk.WebUrl, "/",
            new AssessmentDiscoveryWriter(database.CreateContext)).RunAsync(provider, CancellationToken.None);

        using var db = database.CreateContext();
        var reference = await db.ClassicPageDiscoveries.SingleAsync(row => row.ScanId == scan && row.RowType == "Reference");
        reference.DiscoveryStatus.Should().Be("Discovered");
        reference.FileUniqueId.Should().Be(OfflineFileSdk.FileId);
        reference.ErrorStage.Should().BeNull();
        reference.ErrorDetail.Should().BeNull();
        var page = await db.ClassicPageDiscoveries.SingleAsync(row => row.ScanId == scan && row.RowType == "Page");
        page.FileUniqueId.Should().Be(OfflineFileSdk.FileId);
        page.Url.Should().Be(OfflineFileSdk.FilePath);
        page.DiscoveryStatus.Should().Be("Discovered");
        page.ListId.Should().BeNull();
        page.ListItemId.Should().BeNull();
        page.HomePage.Should().BeTrue();
        sdk.Transport.FileRequests.Should().ContainSingle("targeted HOME discovery reuses the resolved physical record");
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Real_http_exception_secrets_are_redacted_before_reference_database_persistence()
    {
        const string bearer = "fixture-bearer-do-not-persist";
        const string jwt = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJvZmZsaW5lIn0.signature";
        const string password = "fixture-password-do-not-persist";
        const string secret = "fixture-clientsecret-do-not-persist";
        using var sdk = await OfflineFileSdk.CreateAsync();
        sdk.Transport.FileStatus = HttpStatusCode.InternalServerError;
        sdk.Transport.FileJson = ErrorPayload("Offline safe failure; Authorization: Bearer " + bearer +
            "; access_token=" + jwt + "; password=" + password + "; client_secret=" + secret);
        var scan = Guid.NewGuid();
        using var provider = Provider(sdk);

        await new AssessmentWebDiscovery(scan, OfflineFileSdk.WebUrl, "/",
            new AssessmentDiscoveryWriter(database.CreateContext)).RunAsync(provider, CancellationToken.None);

        using var db = database.CreateContext();
        var reference = await db.ClassicPageDiscoveries.SingleAsync(row => row.ScanId == scan && row.RowType == "Reference");
        reference.ErrorDetail.Should().Contain("SharePointRestServiceException").And.Contain("Offline safe failure")
            .And.Contain("500").And.Contain("redacted");
        foreach (var sensitive in new[] { bearer, jwt, password, secret })
        {
            reference.ErrorDetail.Should().NotContain(sensitive);
            reference.EvidenceJson.Should().NotContain(sensitive);
        }
        reference.ErrorDetail.Length.Should().BeLessThanOrEqualTo(1024);
        reference.ErrorDetail.Should().NotContain(" at PnP.");
        sdk.Transport.FileRequests.Should().ContainSingle();
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    private static SharePointLiveAspxDiscoveryProvider Provider(OfflineFileSdk sdk) => new(
        new("offline-app", "scheduled-web", "test-build", "test-authority", Intent: AspxDiscoveryIntent.HomePageOnly),
        new SingleClientFactory(sdk.Client),
        new AspxWebAcquisitionContext(OfflineFileSdk.SiteId, new Uri(OfflineFileSdk.WebUrl),
            OfflineFileSdk.WebId, new Uri(OfflineFileSdk.WebUrl), "/sites/reference", "STS#0"));

    private static void SetFailure(OfflineFileSdk sdk, HttpStatusCode status)
    {
        sdk.Transport.FileStatus = status;
        sdk.Transport.FileJson = ErrorPayload("Offline physical-file request failed.");
    }

    private static string ErrorPayload(string message, string code = "-2147467259, Offline.FileFailure") =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["odata.error"] = new { code, message = new { lang = "en-US", value = message } }
        });

    private sealed class SingleClientFactory(ISharePointAspxRestClient client) : ISharePointAspxRestClientFactory
    {
        public Task<ISharePointAspxRestClient> GetAsync(Uri webUrl, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            webUrl.Should().Be(new Uri(OfflineFileSdk.WebUrl), "the fixture must never alias another Web");
            return Task.FromResult(client);
        }
        public void Dispose() => client.Dispose();
    }
}
