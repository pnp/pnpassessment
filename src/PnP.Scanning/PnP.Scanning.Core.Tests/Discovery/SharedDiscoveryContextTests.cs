using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PnP.Core.Auth;
using PnP.Core.Services;
using PnP.Scanning.Core.Discovery;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed class SharedDiscoveryContextTests
{
    internal const string WebUrl = "https://contoso.sharepoint.com/sites/reuse";
    internal static readonly Guid ListId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid FolderId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task Exact_normalized_web_borrows_one_context_and_discovery_disposal_does_not_dispose_it()
    {
        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        var initializationRequests = sdk.Transport.LogicalRequests.Count;
        var factory = new PnPContextSharePointAspxRestClientFactory(sdk.Factory, sdk.Authentication,
            sharedContext: context);
        var first = await factory.GetAsync(new Uri(WebUrl + "/"));
        var again = await factory.GetAsync(new Uri(WebUrl.ToUpperInvariant()));
        first.Should().BeSameAs(again);
        sdk.Created.Should().ContainSingle().Which.Should().BeSameAs(context);
        sdk.Transport.LogicalRequests.Should().HaveCount(initializationRequests,
            "borrowing an initialized context must not initialize a second one");

        var page = await first.GetPageAsync(new Uri(WebUrl + "/_api/Web?$select=Id"));
        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        first.Dispose();
        IsDisposed(context).Should().BeFalse();
        factory.Dispose();
        IsDisposed(context).Should().BeFalse("the scanner, not discovery, owns this context");

        // Prove the retained context still routes real authenticated REST reads after factory disposal.
        using var retained = new PnPContextSharePointAspxRestClient(context, ownsContext: false);
        (await retained.GetPageAsync(new Uri(WebUrl + "/_api/Web?$select=Id"))).Outcome
            .Should().Be(DiscoveryTerminalOutcome.Complete);
        sdk.Transport.LogicalRequests.Should().HaveCount(initializationRequests + 2);
        sdk.Transport.Unexpected.Should().BeEmpty();
        context.Dispose();
        IsDisposed(context).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/reuse/sub")]
    [InlineData("https://contoso.sharepoint.com/sites/reuse-other")]
    public async Task Different_webs_are_not_aliased_and_factory_owned_contexts_are_disposed(string otherWeb)
    {
        using var sdk = new OfflineSdk();
        var shared = await sdk.CreateAsync();
        var scan = Guid.NewGuid();
        var factory = new PnPContextSharePointAspxRestClientFactory(sdk.Factory, sdk.Authentication, scan, shared);
        var borrowed = await factory.GetAsync(new Uri(WebUrl));
        var other = await factory.GetAsync(new Uri(otherWeb));
        var cached = await factory.GetAsync(new Uri(otherWeb + "/"));
        other.Should().NotBeSameAs(borrowed).And.BeSameAs(cached);
        other.WebUrl.Should().Be(new Uri(otherWeb));
        sdk.Created.Should().HaveCount(2);
        var owned = sdk.Created.Single(context => context.Uri == new Uri(otherWeb));
        owned.Should().NotBeSameAs(shared);
        owned.Properties[Constants.PnPContextPropertyScanId].Should().Be(scan);
        sdk.Transport.LogicalRequests.Count(request => request.Uri.AbsoluteUri.StartsWith(otherWeb + "/_api/",
            StringComparison.OrdinalIgnoreCase)).Should().Be(2, "the other Web gets exactly its own Site/Web initialization");
        factory.Dispose();
        IsDisposed(owned).Should().BeTrue();
        IsDisposed(shared).Should().BeFalse();
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Ordinary_client_still_owns_its_context_by_default()
    {
        using var sdk = new OfflineSdk();
        var context = await sdk.CreateAsync();
        IsDisposed(context).Should().BeFalse();
        var client = new PnPContextSharePointAspxRestClient(context);
        (await client.GetPageAsync(new Uri(WebUrl + "/_api/Web?$select=Id"))).Outcome
            .Should().Be(DiscoveryTerminalOutcome.Complete);
        client.Dispose();
        client.Dispose();
        IsDisposed(context).Should().BeTrue();
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Offline_batch_routes_each_embedded_get_in_order_and_returns_parseable_multipart()
    {
        using var transport = new OfflineTransport();
        using var http = new HttpMessageInvoker(transport, disposeHandler: false);
        using var request = BatchRequest("GET " + WebUrl + "/_api/Site?$select=Id HTTP/1.1",
            "GET " + WebUrl + "/_api/Web?$select=Id HTTP/1.1");
        using var response = await http.SendAsync(request, CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType.MediaType.Should().Be("multipart/mixed");
        var body = await response.Content.ReadAsStringAsync();
        var parts = body.Split("--batchresponse_offline", StringSplitOptions.None)
            .Where(part => part.Contains("HTTP/1.1")).ToArray();
        parts.Should().HaveCount(2);
        using var site = JsonDocument.Parse(parts[0][parts[0].IndexOf('{')..].Trim());
        using var web = JsonDocument.Parse(parts[1][parts[1].IndexOf('{')..].Trim());
        site.RootElement.GetProperty("Id").GetString().Should().Be(OfflineTransport.SiteId);
        web.RootElement.GetProperty("Id").GetString().Should().Be(OfflineTransport.WebId);
        transport.LogicalRequests.Select(entry => entry.Uri.AbsolutePath)
            .Should().Equal("/sites/reuse/_api/Site", "/sites/reuse/_api/Web");
        transport.LogicalRequests.Should().OnlyContain(entry => entry.InBatch);
        transport.BatchRequests.Should().Be(1);
        transport.Unexpected.Should().BeEmpty();
    }

    [Theory]
    [InlineData("GET https://graph.microsoft.com/v1.0/sites HTTP/1.1")]
    [InlineData("GET https://contoso.sharepoint.com/sites/reuse/_api/Unknown HTTP/1.1")]
    [InlineData("POST https://contoso.sharepoint.com/sites/reuse/_api/Web HTTP/1.1")]
    public async Task Offline_batch_rejects_unknown_hosts_paths_and_mutations(string embedded)
    {
        using var transport = new OfflineTransport();
        using var http = new HttpMessageInvoker(transport, disposeHandler: false);
        using var request = BatchRequest(embedded);
        var send = () => http.SendAsync(request, CancellationToken.None);
        await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("Offline fixture rejected*");
        transport.Unexpected.Should().ContainSingle();
    }

    private static HttpRequestMessage BatchRequest(params string[] requests)
    {
        const string boundary = "batch_test";
        var body = string.Join("", requests.Select(request => "--" + boundary + "\r\n" +
            "Content-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n\r\n" +
            request + "\r\nAccept: application/json;odata=nometadata\r\n\r\n")) + "--" + boundary + "--\r\n";
        var message = new HttpRequestMessage(HttpMethod.Post, WebUrl + "/_api/$batch")
        {
            Content = new StringContent(body, Encoding.UTF8)
        };
        message.Content.Headers.ContentType = new("multipart/mixed");
        message.Content.Headers.ContentType.Parameters.Add(new("boundary", boundary));
        return message;
    }

    private static bool IsDisposed(PnPContext context)
    {
        // The SDK's Dispose only changes this lifecycle flag; it deliberately leaves HTTP clients usable.
        // Inspect actual lifecycle state instead of assuming a subsequent successful read proves ownership.
        var flag = typeof(PnPContext).GetField("disposed", BindingFlags.Instance | BindingFlags.NonPublic);
        flag.Should().NotBeNull("the ownership assertion must fail visibly if the SDK lifecycle contract changes");
        return (bool)flag.GetValue(context);
    }

    internal sealed class OfflineSdk : IDisposable
    {
        private readonly ServiceProvider services;
        internal readonly OfflineTransport Transport = new();
        internal readonly IAuthenticationProvider Authentication = new ExternalAuthenticationProvider((_, _) =>
            Task.FromResult(new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims:
                new[] { new Claim("roles", "Sites.Read.All") }))));
        internal readonly IPnPContextFactory Factory;
        private readonly RecordingContextFactory recording;
        internal IReadOnlyList<PnPContext> Created => recording.Created;

        internal OfflineSdk()
        {
            var collection = new ServiceCollection();
            collection.AddPnPCore(options => options.DisableTelemetry = true,
                configureSharePointRest: builder => builder.ConfigurePrimaryHttpMessageHandler(() => Transport),
                configureMicrosoftGraph: builder => builder.ConfigurePrimaryHttpMessageHandler(() => Transport));
            services = collection.BuildServiceProvider();
            Factory = DispatchProxy.Create<IPnPContextFactory, RecordingContextFactory>();
            recording = (RecordingContextFactory)(object)Factory;
            recording.Inner = services.GetRequiredService<IPnPContextFactory>();
        }

        internal Task<PnPContext> CreateAsync() => Factory.CreateAsync(new Uri(WebUrl), Authentication,
            CancellationToken.None, new PnPContextOptions
            {
                AdditionalWebPropertiesOnCreate = new System.Linq.Expressions.Expression<Func<PnP.Core.Model.SharePoint.IWeb, object>>[]
                { web => web.ServerRelativeUrl }
            });

        public void Dispose()
        {
            foreach (var context in Created) context.Dispose();
            services.Dispose();
        }
    }

    public class RecordingContextFactory : DispatchProxy
    {
        internal IPnPContextFactory Inner { get; set; }
        internal List<PnPContext> Created { get; } = new();

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == nameof(IPnPContextFactory.CreateAsync) && args.Length == 4 && args[0] is Uri uri)
                return CreateAsync(uri, (IAuthenticationProvider)args[1], (CancellationToken)args[2],
                    (PnPContextOptions)args[3]);
            throw new InvalidOperationException("Unexpected factory operation: " + method.Name);
        }

        private async Task<PnPContext> CreateAsync(Uri uri, IAuthenticationProvider authentication,
            CancellationToken token, PnPContextOptions options)
        {
            var context = await Inner.CreateAsync(uri, authentication, token, options);
            Created.Add(context);
            return context;
        }
    }

    internal sealed class OfflineTransport : HttpMessageHandler
    {
        internal const string SiteId = "11111111-1111-1111-1111-111111111111";
        internal const string WebId = "22222222-2222-2222-2222-222222222222";
        internal readonly List<(Uri Uri, bool InBatch)> LogicalRequests = new();
        internal readonly List<string> Unexpected = new();
        internal int BatchRequests { get; private set; }
        internal string ListResponse { get; set; } = "complete";
        internal bool FailMetadata { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri.Host != "contoso.sharepoint.com") throw Reject(request.Method + " " + request.RequestUri);
            if (request.Method == HttpMethod.Get)
            {
                var result = Route(request.RequestUri, inBatch: false);
                return JsonResponse(result.Status, result.Json);
            }
            if (request.Method != HttpMethod.Post || !request.RequestUri.AbsolutePath.EndsWith("/_api/$batch",
                StringComparison.OrdinalIgnoreCase)) throw Reject(request.Method + " " + request.RequestUri);
            if (request.Content?.Headers.ContentType?.MediaType != "multipart/mixed") throw Reject("non-multipart batch");
            var boundary = request.Content.Headers.ContentType.Parameters.Single(parameter => parameter.Name == "boundary")
                .Value.Trim('"');
            var body = await request.Content.ReadAsStringAsync(token);
            var parts = body.Split("--" + boundary, StringSplitOptions.None);
            if (parts.Length < 3 || parts[0].Length != 0 || parts[^1].Trim() != "--") throw Reject("malformed batch framing");
            BatchRequests++;
            var output = new StringBuilder();
            foreach (var part in parts.Skip(1).SkipLast(1))
            {
                var matches = Regex.Matches(part, @"(?m)^(GET|POST|PATCH|DELETE|PUT|HEAD|OPTIONS) ([^\r\n]+) HTTP/1\.1\r?$");
                if (!part.Contains("Content-Type: application/http", StringComparison.OrdinalIgnoreCase) ||
                    matches.Count != 1 || matches[0].Groups[1].Value != "GET" || part.Contains("changeset_", StringComparison.Ordinal))
                    throw Reject("unsupported batch part: " + part.Trim());
                var uri = new Uri(matches[0].Groups[2].Value, UriKind.Absolute);
                var result = Route(uri, inBatch: true);
                output.Append("--batchresponse_offline\r\nContent-Type: application/http\r\n")
                    .Append("Content-Transfer-Encoding: binary\r\n\r\nHTTP/1.1 ")
                    .Append((int)result.Status).Append(result.Status == HttpStatusCode.OK ? " OK" : " Forbidden")
                    .Append("\r\nContent-Type: application/json;odata=nometadata\r\n\r\n")
                    .Append(result.Json).Append("\r\n");
            }
            output.Append("--batchresponse_offline--\r\n");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(output.ToString(), Encoding.UTF8) };
            response.Content.Headers.ContentType = new("multipart/mixed");
            response.Content.Headers.ContentType.Parameters.Add(new("boundary", "batchresponse_offline"));
            return response;
        }

        private (HttpStatusCode Status, string Json) Route(Uri uri, bool inBatch)
        {
            LogicalRequests.Add((uri, inBatch));
            if (uri.Host != "contoso.sharepoint.com" || uri.Scheme != Uri.UriSchemeHttps) throw Reject("GET " + uri);
            var api = uri.AbsolutePath.IndexOf("/_api/", StringComparison.OrdinalIgnoreCase);
            if (api < 0) throw Reject("GET " + uri);
            var endpoint = uri.AbsolutePath[(api + 6)..];
            var currentWeb = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath[..api];
            if (!currentWeb.Equals(WebUrl, StringComparison.OrdinalIgnoreCase) &&
                !currentWeb.Equals(WebUrl + "/sub", StringComparison.OrdinalIgnoreCase) &&
                !currentWeb.Equals(WebUrl + "-other", StringComparison.OrdinalIgnoreCase)) throw Reject("GET " + uri);
            if (endpoint.Equals("Site", StringComparison.OrdinalIgnoreCase))
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    Id = SiteId, GroupId = Guid.Empty, Features = Array.Empty<object>(),
                    RootWeb = new { Id = WebId, Url = currentWeb, ContentTypes = Array.Empty<object>() }
                }));
            if (ListResponse == "displayForm" && endpoint.Equals($"Web/lists(guid'{ListId:D}')/items", StringComparison.OrdinalIgnoreCase))
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new { value = new[]
                {
                    new { Id = 7, ContentTypeId = "0x0110007C4E8A" },
                    new { Id = 9, ContentTypeId = "0x01" },
                } }));
            if (!endpoint.Equals("Web", StringComparison.OrdinalIgnoreCase)) throw Reject("GET " + uri);
            var web = new Dictionary<string, object>
            {
                ["Id"] = WebId, ["Url"] = currentWeb, ["ServerRelativeUrl"] = new Uri(currentWeb).AbsolutePath,
                ["Title"] = "Offline Web", ["WebTemplateConfiguration"] = "STS#0",
                ["RegionalSettings"] = new { TimeZone = new { Id = 2 } },
                ["WelcomePage"] = ListResponse == "displayForm" ? "" : "SitePages/Home.aspx", ["Features"] = Array.Empty<object>(),
                ["LastItemUserModifiedDate"] = "2026-10-20T00:00:00Z"
            };
            if (HasListProjection(uri))
            {
                if (FailMetadata) return (HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"accessDenied\",\"message\":\"metadata denied\"}}");
                if (ListResponse != "missing")
                {
                    var list = new Dictionary<string, object>
                    {
                        ["Id"] = ListId, ["Title"] = "Documents", ["BaseType"] = 1, ["BaseTemplate"] = 101,
                        ["Hidden"] = false, ["IsCatalog"] = false, ["DefaultViewUrl"] = "/sites/reuse/Documents/Forms/AllItems.aspx",
                        ["DefaultDisplayFormUrl"] = "/sites/reuse/Documents/Forms/DispForm.aspx",
                        ["RootFolder"] = ListResponse == "partial"
                            ? new { ServerRelativeUrl = "/sites/reuse/Documents" }
                            : (object)new { ServerRelativeUrl = "/sites/reuse/Documents", UniqueId = FolderId },
                        ["ItemCount"] = 7,
                        ["Fields"] = new[] { new { Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                            InternalName = "Title", Title = "Title", FieldTypeKind = 2, TypeAsString = "Text" } },
                        ["ContentTypes"] = new[] { new { Id = new { StringValue = "0x0101" }, StringId = "0x0101", Name = "Document",
                            DocumentTemplateUrl = "", DisplayFormUrl = "/sites/reuse/Documents/Forms/DispForm.aspx" } }
                    };
                    if (ListResponse == "displayForm")
                    {
                        list["Title"] = "Posts";
                        list["BaseType"] = 0;
                        list["BaseTemplate"] = 100;
                        list["RootFolder"] = new { ServerRelativeUrl = "/sites/reuse/Lists/Posts", UniqueId = FolderId };
                        list["DefaultDisplayFormUrl"] = "/sites/reuse/Lists/Posts/DispForm.aspx";
                        list["ContentTypes"] = new[] { new { Id = new { StringValue = "0x0110" }, StringId = "0x0110",
                            Name = "Post", DisplayFormUrl = "Lists/Posts/Post.aspx" } };
                    }
                    if (ListResponse == "emptyContentTypes") list["ContentTypes"] = Array.Empty<object>();
                    if (ListResponse == "missingContentTypes") list.Remove("ContentTypes");
                    if (ListResponse == "partialContentTypes")
                        list["ContentTypes"] = new[] { new { Id = new { StringValue = "0x0101" }, StringId = "0x0101" } };
                    if (ListResponse == "continuedContentTypes")
                        list["ContentTypes@odata.nextLink"] = WebUrl + "/_api/Web/Lists/ContentTypes?$skiptoken=next";
                    web["Lists"] = ListResponse == "empty" ? Array.Empty<object>() : new[] { list };
                    if (ListResponse == "nextLink") web["Lists@odata.nextLink"] = WebUrl + "/_api/Web/Lists?$skiptoken=next";
                }
            }
            return (HttpStatusCode.OK, JsonSerializer.Serialize(web));
        }

        internal static bool HasListProjection(Uri uri) => Uri.UnescapeDataString(uri.Query)
            .Contains("Lists", StringComparison.OrdinalIgnoreCase);

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private Exception Reject(string detail)
        {
            Unexpected.Add(detail);
            return new InvalidOperationException("Offline fixture rejected unexpected request: " + detail);
        }
    }
}
