using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SharePoint.Client;
using PnP.Core;
using PnP.Core.Services;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages;

public class PublishingLayoutTransportTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, 2)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, 2)]
    [InlineData(HttpStatusCode.TooManyRequests, false, 3)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, 3)]
    [InlineData(HttpStatusCode.Forbidden, false, 1)]
    [InlineData(HttpStatusCode.NotFound, false, 1)]
    public async Task Csom_fallback_retries_transient_responses_before_caching_the_result(
        HttpStatusCode status, bool recover, int expectedRequests)
    {
        using var transport = new LayoutResponseHandler(status, recover);
        var services = new ServiceCollection();
        services.AddPnPCore(options =>
        {
            options.DisableTelemetry = true;
            options.HttpRequests.SharePointRest.MaxRetries = 2;
            options.HttpRequests.SharePointRest.DelayInSeconds = 0;
        }, configureSharePointRest: builder =>
            builder.ConfigurePrimaryHttpMessageHandler(() => transport));
        using var provider = services.BuildServiceProvider();
        using var httpClient = provider.GetRequiredService<SharePointRestClient>().Client;
        const string site = "https://contoso.sharepoint.com/sites/a";
        using var context = new ClientContext(site)
        {
            WebRequestExecutorFactory = new HttpClientWebRequestExecutorFactory(httpClient),
        };
        var reader = new PublishingLayoutStatusReader(new Uri(site), async url =>
        {
            var file = context.Site.RootWeb.GetFileByServerRelativeUrl(url);
            context.Load(file, value => value.CustomizedPageStatus);
            await context.ExecuteQueryAsync();
            return file.CustomizedPageStatus.ToString();
        });

        foreach (var web in new[] { "/first", "/second" })
        {
            var page = new ClassicPage
            {
                PageType = PageScanComponent.PublishingPage, WebUrl = web,
                LayoutUrl = "/sites/a/_catalogs/masterpage/Basic.aspx",
            };
            if (recover)
            {
                await reader.EnrichAsync(page);
                page.LayoutCustomizedPageStatus.Should().Be("Uncustomized");
            }
            else
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() => reader.EnrichAsync(page));
                if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                    error.Should().BeOfType<ServiceException>();
                else
                    error.Should().BeOfType<ClientRequestException>();
                page.LayoutCustomizedPageStatus.Should().BeNull();
            }
        }

        transport.RequestCount.Should().Be(expectedRequests,
            "shared layouts must not bypass SDK retries or restart an exhausted retry budget");
    }

    private sealed class LayoutResponseHandler(HttpStatusCode failure, bool recover) : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (!recover || RequestCount == 1)
            {
                var response = new HttpResponseMessage(failure)
                {
                    Content = new StringContent("Layout request failed"),
                };
                response.Headers.TryAddWithoutValidation("Retry-After", "0");
                return response;
            }

            var xml = XDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            var query = xml.Descendants().Single(element =>
                element.Name.LocalName == "Query" && element.Attribute("Id") != null);
            var body = $$"""
                [{"SchemaVersion":"15.0.0.0","LibraryVersion":"16.0.0.0","ErrorInfo":null},
                 {{query.Attribute("Id").Value}},
                 {"_ObjectType_":"SP.File","_ObjectIdentity_":"layout-file","CustomizedPageStatus":1}]
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
