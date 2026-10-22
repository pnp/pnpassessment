using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PnP.Core.Auth;
using PnP.Core.Model.SharePoint;
using PnP.Core.Services;
using PnP.Scanning.Core.Discovery;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed class ReferenceFileResolutionTests
{
    [Theory]
    [InlineData("omitted")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("odata-null")]
    public async Task Http_200_physical_file_survives_absent_or_unloaded_list_item_metadata(string childShape)
    {
        using var sdk = await OfflineFileSdk.CreateAsync();
        sdk.Transport.FileJson = FilePayload(childShape);

        var result = await sdk.Client.ResolveFileAsync(OfflineFileSdk.FilePath);

        AssertPhysicalFile(result);
        result.ListId.Should().Be(OfflineFileSdk.ListId);
        result.ListItemId.Should().BeNull();
        sdk.Transport.FileRequests.Should().ContainSingle();
        sdk.Transport.Requests.Should().HaveCount(3, "only Web/Site initialization plus one file GET is allowed");
        sdk.Transport.ReturnedFileStatuses.Should().Equal(HttpStatusCode.OK);
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    public async Task Unavailable_or_empty_list_id_is_optional_and_does_not_discard_a_physical_file(string listShape)
    {
        using var sdk = await OfflineFileSdk.CreateAsync();
        sdk.Transport.FileJson = FilePayload("omitted", listShape);

        var result = await sdk.Client.ResolveFileAsync(OfflineFileSdk.FilePath);

        AssertPhysicalFile(result);
        result.ListId.Should().BeNull();
        result.ListItemId.Should().BeNull();
        sdk.Transport.FileRequests.Should().ContainSingle();
        sdk.Transport.Requests.Should().HaveCount(3);
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Theory]
    [InlineData(23, 23)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    public async Task Only_positive_loaded_item_identity_is_projected_and_real_list_identity_is_preserved(int itemId, int? expected)
    {
        using var sdk = await OfflineFileSdk.CreateAsync();
        sdk.Transport.FileJson = FilePayload("item", itemId: itemId);

        var result = await sdk.Client.ResolveFileAsync(OfflineFileSdk.FilePath);

        AssertPhysicalFile(result);
        result.ListId.Should().Be(OfflineFileSdk.ListId);
        result.ListItemId.Should().Be(expected);
        sdk.Transport.FileRequests.Should().ContainSingle();
        sdk.Transport.Requests.Should().HaveCount(3);
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Actual_sdk_child_stub_without_loaded_id_does_not_throw_during_projection()
    {
        using var sdk = await OfflineFileSdk.CreateAsync();
        sdk.Transport.FileJson = FilePayload("omitted", "missing");
        var file = await sdk.Context.Web.GetFileByServerRelativeUrlOrDefaultAsync(OfflineFileSdk.FilePath,
            value => value.UniqueId, value => value.Name, value => value.ServerRelativeUrl,
            value => value.CustomizedPageStatus, value => value.ListId, value => value.ListItemAllFields);
        file.Should().NotBeNull();
        file.IsPropertyAvailable(value => value.ListId).Should().BeFalse();
        var child = file.ListItemAllFields;
        child.Should().NotBeNull("the real SDK materializes an unloaded child model, not a CLR null");
        child.IsPropertyAvailable(value => value.Id).Should().BeFalse();
        SharePointResolvedFile result = null;
        Action project = () => result = PnPContextSharePointAspxRestClient.MapResolvedFile(file);

        project.Should().NotThrow();

        AssertPhysicalFile(result);
        result.ListId.Should().BeNull();
        result.ListItemId.Should().BeNull();
        sdk.Transport.Requests.Should().HaveCount(3, "checking optional availability must never lazy-load metadata");
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Pre_canceled_resolution_does_not_send_a_file_request()
    {
        using var sdk = await OfflineFileSdk.CreateAsync();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var resolve = () => sdk.Client.ResolveFileAsync(OfflineFileSdk.FilePath, cancel.Token);

        await resolve.Should().ThrowAsync<OperationCanceledException>();

        sdk.Transport.FileRequests.Should().BeEmpty();
        sdk.Transport.Requests.Should().HaveCount(2);
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_during_real_sdk_http_request_is_not_converted_to_a_failed_reference()
    {
        using var sdk = await OfflineFileSdk.CreateAsync();
        using var cancel = new CancellationTokenSource();
        sdk.Transport.OnFileRequest = () =>
        {
            cancel.Cancel();
            throw new OperationCanceledException("Offline HTTP cancellation", cancel.Token);
        };
        var resolve = () => sdk.Client.ResolveFileAsync(OfflineFileSdk.FilePath, cancel.Token);

        await resolve.Should().ThrowAsync<OperationCanceledException>();

        sdk.Transport.FileRequests.Should().ContainSingle();
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    internal static void AssertPhysicalFile(SharePointResolvedFile result)
    {
        result.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        result.FileUniqueId.Should().Be(OfflineFileSdk.FileId.ToString("D"));
        result.Name.Should().Be("Page.aspx");
        result.ServerRelativeUrl.Should().Be(OfflineFileSdk.FilePath);
        result.CustomizedPageStatus.Should().Be(nameof(CustomizedPageStatus.Uncustomized));
        result.ErrorCode.Should().BeNull();
        result.ErrorStage.Should().BeNull();
        result.ErrorDetail.Should().BeNull();
    }

    internal static string FilePayload(string childShape = "omitted", string listShape = "positive", int itemId = 23,
        bool omitName = false)
    {
        var file = new Dictionary<string, object>
        {
            ["UniqueId"] = OfflineFileSdk.FileId, ["ServerRelativeUrl"] = OfflineFileSdk.FilePath,
            ["CustomizedPageStatus"] = 1
        };
        if (!omitName) file["Name"] = "Page.aspx";
        if (listShape != "missing") file["ListId"] = listShape == "empty" ? Guid.Empty : OfflineFileSdk.ListId;
        if (childShape != "omitted") file["ListItemAllFields"] = childShape switch
        {
            "empty" => new Dictionary<string, object>(),
            "null" => null,
            "odata-null" => new Dictionary<string, object> { ["odata.null"] = true },
            "item" => new Dictionary<string, object> { ["Id"] = itemId },
            _ => throw new ArgumentOutOfRangeException(nameof(childShape))
        };
        return JsonSerializer.Serialize(file);
    }

    // This fixture is intentionally independent of the prior request-reuse branch: no borrowed ctor,
    // metadata helper, proxy model, live auth, Graph fallback, batch catch-all or external test file.
    internal sealed class OfflineFileSdk : IDisposable
    {
        internal const string WebUrl = "https://contoso.sharepoint.com/sites/reference";
        internal const string FilePath = "/sites/reference/SitePages/Page.aspx";
        internal static readonly Guid SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        internal static readonly Guid WebId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        internal static readonly Guid FileId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        internal static readonly Guid ListId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        private readonly ServiceProvider services;
        internal readonly FileTransport Transport = new();
        internal PnPContext Context { get; private set; }
        internal PnPContextSharePointAspxRestClient Client { get; private set; }

        private OfflineFileSdk()
        {
            var collection = new ServiceCollection();
            collection.AddPnPCore(options =>
            {
                options.DisableTelemetry = true;
                options.PnPContext.GraphFirst = false;
                options.HttpRequests.SharePointRest.MaxRetries = 0;
                options.HttpRequests.SharePointRest.DelayInSeconds = 0;
            }, configureSharePointRest: builder => builder.ConfigurePrimaryHttpMessageHandler(() => Transport),
                configureMicrosoftGraph: builder => builder.ConfigurePrimaryHttpMessageHandler(() => Transport));
            services = collection.BuildServiceProvider();
        }

        internal static async Task<OfflineFileSdk> CreateAsync()
        {
            var sdk = new OfflineFileSdk();
            try
            {
                var auth = new ExternalAuthenticationProvider((_, _) => Task.FromResult(
                    new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims:
                        new[] { new Claim("roles", "Sites.Read.All") }))));
                sdk.Context = await sdk.services.GetRequiredService<IPnPContextFactory>().CreateAsync(new Uri(WebUrl), auth);
                sdk.Client = new PnPContextSharePointAspxRestClient(sdk.Context);
                return sdk;
            }
            catch { sdk.Dispose(); throw; }
        }

        public void Dispose() { Client?.Dispose(); services.Dispose(); }
    }

    internal sealed class FileTransport : HttpMessageHandler
    {
        internal readonly List<Uri> Requests = new();
        internal readonly List<Uri> FileRequests = new();
        internal readonly List<HttpStatusCode> ReturnedFileStatuses = new();
        internal readonly List<string> Unexpected = new();
        internal string FileJson { get; set; } = FilePayload();
        internal HttpStatusCode FileStatus { get; set; } = HttpStatusCode.OK;
        internal Action OnFileRequest { get; set; }
        internal const string RequestId = "55555555-5555-5555-5555-555555555555";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri);
            var uri = request.RequestUri;
            if (request.Method != HttpMethod.Get || uri.Scheme != Uri.UriSchemeHttps ||
                uri.Host != "contoso.sharepoint.com") throw Reject(request);
            string json;
            var status = HttpStatusCode.OK;
            if (uri.AbsolutePath.Equals("/sites/reference/_api/Web", StringComparison.OrdinalIgnoreCase))
                json = JsonSerializer.Serialize(new
                {
                    Id = OfflineFileSdk.WebId, Url = OfflineFileSdk.WebUrl, ServerRelativeUrl = "/sites/reference",
                    WebTemplateConfiguration = "STS#0", RegionalSettings = new { TimeZone = new { Id = 2 } },
                    WelcomePage = "SitePages/Page.aspx"
                });
            else if (uri.AbsolutePath.Equals("/sites/reference/_api/Site", StringComparison.OrdinalIgnoreCase))
                json = JsonSerializer.Serialize(new { Id = OfflineFileSdk.SiteId, GroupId = Guid.Empty });
            else if (uri.AbsolutePath.Equals("/sites/reference/_api/Web/getFileByServerRelativePath(decodedUrl=@u)",
                StringComparison.OrdinalIgnoreCase) && Uri.UnescapeDataString(uri.Query)
                    .Contains("@u='" + OfflineFileSdk.FilePath + "'", StringComparison.Ordinal))
            {
                FileRequests.Add(uri);
                OnFileRequest?.Invoke();
                json = FileJson;
                status = FileStatus;
                ReturnedFileStatuses.Add(status);
            }
            else throw Reject(request);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            response.Headers.Add("SPRequestGuid", RequestId);
            return Task.FromResult(response);
        }

        private Exception Reject(HttpRequestMessage request)
        {
            var message = request.Method + " " + request.RequestUri;
            Unexpected.Add(message);
            return new InvalidOperationException("Offline fixture rejected unexpected request: " + message);
        }
    }
}
