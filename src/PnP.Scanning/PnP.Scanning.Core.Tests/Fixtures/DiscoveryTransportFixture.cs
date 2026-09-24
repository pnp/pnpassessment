using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PnP.Core.Services;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PnP.Scanning.Core.Tests.Fixtures;

// The real SDK factory, default headers and retry pipeline end in this offline transport.
// No credentials, sockets, reflection into SDK internals, or version-string substitutes.
internal sealed class DiscoveryTransportFixture : IDisposable
{
    internal static readonly Uri WebUrl = new("https://example.com/sites/discovery");
    internal const string SiteId = "11111111-1111-1111-1111-111111111111";
    internal const string WebId = "22222222-2222-2222-2222-222222222222";
    internal const string FileId = "33333333-3333-3333-3333-333333333333";
    internal const string FolderId = "44444444-4444-4444-4444-444444444444";
    private readonly ServiceProvider services;

    internal DiscoveryTransportFixture(string configuredUserAgent = null)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddPnPCore(options =>
        {
            options.DisableTelemetry = true;
            options.PnPContext.GraphFirst = false;
            if (configuredUserAgent != null) options.HttpRequests.UserAgent = configuredUserAgent;
        });
        DiscoveryTestTrafficHandler.Register(collection);
        collection.AddHttpClient<SharePointRestClient>().ConfigurePrimaryHttpMessageHandler(() => Transport);
        collection.AddHttpClient<MicrosoftGraphClient>().ConfigurePrimaryHttpMessageHandler(() => Transport);
        services = collection.BuildServiceProvider();
        ContextFactory = services.GetRequiredService<IPnPContextFactory>();
        DefaultUserAgent = services.GetRequiredService<IOptions<PnPGlobalSettingsOptions>>().Value.HttpUserAgent;
    }

    internal RecordingTransport Transport { get; } = new();
    internal FixtureAuthentication Authentication { get; } = new();
    internal IPnPContextFactory ContextFactory { get; }
    internal string DefaultUserAgent { get; }

    internal PnPContextSharePointAspxRestClientFactory CreateFactory(bool? enabled, Guid? scanId = null)
    {
        var request = new StartRequest { Mode = Mode.Classic.ToString() };
        if (enabled.HasValue)
        {
            ClassicStartRequestBuilder.AddClassicProperties(request, new[] { ClassicComponent.Pages },
                false, false, false, false, discoveryTestTraffic: enabled.Value);
            request.Properties.Single(p => p.Property == Constants.StartClassicDiscoveryTestTraffic)
                .Value.Should().Be(enabled.Value.ToString());
        }
        var options = (ClassicOptions)OptionsBase.FromScannerInput(request);
        options.DiscoveryTestTraffic.Should().Be(enabled ?? false);
        // This is the same options-to-factory boundary used by scheduled discovery execution.
        return ClassicPageDiscoveryComponent.CreateClientFactory(ContextFactory, Authentication,
            scanId ?? Guid.NewGuid(), options);
    }

    internal void AssertHeaders(bool enabled, IEnumerable<RecordedRequest> requests = null)
    {
        var actual = (requests ?? Transport.Requests).ToArray();
        actual.Should().NotBeEmpty();
        DefaultUserAgent.Should().StartWith("NONISV|SharePointPnP|PnPCoreSDK/");
        foreach (var request in actual)
        {
            request.UserAgent.Should().Be(DefaultUserAgent + (enabled ? " testtraffic-smr" : ""), request.Uri.ToString());
            request.UserAgent.Split(' ').Count(token => token == "testtraffic-smr").Should().Be(enabled ? 1 : 0);
            request.Authorization.Should().Be("Bearer offline-fixture");
        }
    }

    public void Dispose() => services.Dispose();

    internal sealed record RecordedRequest(Uri Uri, string Method, string UserAgent, string Authorization, string Body);

    internal sealed class FixtureAuthentication : IAuthenticationProvider
    {
        internal int Authentications;
        public Task AuthenticateRequestAsync(Uri resource, HttpRequestMessage request)
        {
            Interlocked.Increment(ref Authentications);
            request.Headers.Authorization = new("Bearer", "offline-fixture");
            return Task.CompletedTask;
        }
        public Task<string> GetAccessTokenAsync(Uri resource, string[] scopes) => Task.FromResult("offline-fixture");
        public Task<string> GetAccessTokenAsync(Uri resource) => Task.FromResult("offline-fixture");
    }

    internal sealed class RecordingTransport : HttpMessageHandler
    {
        internal ConcurrentQueue<RecordedRequest> Requests { get; } = new();
        internal Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new(request.RequestUri, request.Method.Method, request.Headers.UserAgent.ToString(),
                request.Headers.Authorization?.ToString(), body));
            if (Respond == null && request.RequestUri.AbsolutePath.EndsWith("/$batch"))
            {
                var response = new StringBuilder();
                foreach (Match part in Regex.Matches(body, @"GET (\S+) HTTP/1.1"))
                {
                    response.Append("--batchresponse_fixture\r\nContent-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n\r\n");
                    response.Append("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n");
                    response.Append(DefaultResponse(new Uri(part.Groups[1].Value))).Append("\r\n");
                }
                response.Append("--batchresponse_fixture--\r\n");
                var result = Json(response.ToString());
                result.Content.Headers.ContentType = new("multipart/mixed");
                result.Content.Headers.ContentType.Parameters.Add(new("boundary", "batchresponse_fixture"));
                return result;
            }
            return Respond?.Invoke(request) ?? Json(DefaultResponse(request.RequestUri));
        }

        internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        private static string DefaultResponse(Uri uri)
        {
            var url = uri.AbsoluteUri;
            object model;
            if (url.Contains("getfileby", StringComparison.OrdinalIgnoreCase))
                model = new { UniqueId = FileId, Name = "default.aspx", ServerRelativeUrl = "/sites/discovery/default.aspx",
                    CustomizedPageStatus = 1, ListId = Guid.Empty, ListItemAllFields = new { Id = 1 } };
            else if (url.Contains("getfolderby", StringComparison.OrdinalIgnoreCase))
                model = new { UniqueId = FolderId, ServerRelativeUrl = "/sites/discovery", Folders = Array.Empty<object>(),
                    Files = new[] { new { UniqueId = FileId, Name = "default.aspx", ServerRelativeUrl = "/sites/discovery/default.aspx", CustomizedPageStatus = 1 } } };
            else if (uri.AbsolutePath.EndsWith("/_api/site", StringComparison.OrdinalIgnoreCase))
                model = new { Id = SiteId, GroupId = Guid.Empty };
            else if (uri.AbsolutePath.EndsWith("/_api/web", StringComparison.OrdinalIgnoreCase))
                model = new { Id = WebId, Url = WebUrl.AbsoluteUri, ServerRelativeUrl = WebUrl.AbsolutePath,
                    WelcomePage = "default.aspx", RegionalSettings = new { TimeZone = new { Id = 2 } } };
            else
                return "{\"value\":[]}";
            return JsonSerializer.Serialize(model);
        }
    }
}
