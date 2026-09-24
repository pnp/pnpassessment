using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Tests.Fixtures;
using System.Net;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

public sealed class DiscoveryTestTrafficTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scheduled_options_cover_initialization_metadata_and_modeled_reads(bool? enabled)
    {
        using var fixture = new DiscoveryTransportFixture();
        var scanId = Guid.NewGuid();
        using var factory = fixture.CreateFactory(enabled, scanId);
        using var metadata = await factory.CreateContextAsync(DiscoveryTransportFixture.WebUrl, CancellationToken.None);
        metadata.Properties[Constants.PnPContextPropertyScanId].Should().Be(scanId);
        var site = await metadata.Site.GetAsync(value => value.Id);
        var web = await metadata.Web.GetAsync(value => value.Id, value => value.Url, value => value.ServerRelativeUrl);
        site.Id.Should().Be(Guid.Parse(DiscoveryTransportFixture.SiteId));
        web.ServerRelativeUrl.Should().Be("/sites/discovery");
        var client = await factory.GetAsync(DiscoveryTransportFixture.WebUrl);
        (await factory.GetAsync(DiscoveryTransportFixture.WebUrl)).Should().BeSameAs(client);

        var welcome = await client.ReadWelcomePageAsync();
        var file = await client.ResolveFileAsync("/sites/discovery/default.aspx");
        var folder = await client.ReadFolderAsync("/sites/discovery");
        welcome.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        file.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        // Preserve the existing outcome: the SDK 1.18.0 IQueryable projection in
        // ReadFolderAsync throws after the successful HTTP read. This header-only
        // change must not silently convert that failure into Empty or Complete.
        folder.Outcome.Should().Be(DiscoveryTerminalOutcome.Failed);
        folder.ErrorCode.Should().Be("web_root_folder_failed");
        folder.EvidenceRef.Should().EndWith(":InvalidCastException");
        fixture.Transport.Requests.Should().Contain(r => r.Uri.AbsoluteUri.Contains("GetFileBy", StringComparison.OrdinalIgnoreCase));
        fixture.Transport.Requests.Should().Contain(r => r.Uri.AbsoluteUri.Contains("GetFolderBy", StringComparison.OrdinalIgnoreCase));
        fixture.AssertHeaders(enabled ?? false);
    }

    [Fact]
    public async Task A_request_user_agent_replaces_rather_than_merges_httpclient_defaults()
    {
        using var fixture = new DiscoveryTransportFixture();
        using var context = await fixture.ContextFactory.CreateAsync(DiscoveryTransportFixture.WebUrl, fixture.Authentication);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(DiscoveryTransportFixture.WebUrl, "/_api/web/lists"));
        request.Headers.UserAgent.ParseAdd("testtraffic-smr"); // Reproduce the original bug at the transport boundary.
        using var response = await context.RestClient.Client.SendAsync(request);
        fixture.Transport.Requests.Last().UserAgent.Should().Be("testtraffic-smr");
        context.RestClient.Client.DefaultRequestHeaders.UserAgent.ToString().Should().Be(fixture.DefaultUserAgent);
    }

    [Fact]
    public async Task Marked_and_unmarked_scans_and_unrelated_traffic_share_clients_without_leaking()
    {
        using var fixture = new DiscoveryTransportFixture();
        using var marked = fixture.CreateFactory(true);
        using var unmarked = fixture.CreateFactory(false);
        using var ordinary = await fixture.ContextFactory.CreateAsync(DiscoveryTransportFixture.WebUrl, fixture.Authentication);
        using var markedContext = await marked.CreateContextAsync(DiscoveryTransportFixture.WebUrl, CancellationToken.None);
        ordinary.RestClient.Should().BeSameAs(markedContext.RestClient);
        fixture.Transport.Requests.Clear();

        await Task.WhenAll(marked.GetAsync(DiscoveryTransportFixture.WebUrl), unmarked.GetAsync(DiscoveryTransportFixture.WebUrl));
        fixture.Transport.Requests.Clear();
        var on = await marked.GetAsync(DiscoveryTransportFixture.WebUrl);
        var off = await unmarked.GetAsync(DiscoveryTransportFixture.WebUrl);
        await Task.WhenAll(
            on.GetPageAsync(new Uri(DiscoveryTransportFixture.WebUrl, "/_api/marked")),
            off.GetPageAsync(new Uri(DiscoveryTransportFixture.WebUrl, "/_api/unmarked")),
            ordinary.Web.GetAsync(value => value.WelcomePage));

        fixture.AssertHeaders(true, fixture.Transport.Requests.Where(r => r.Uri.AbsolutePath == "/_api/marked"));
        fixture.AssertHeaders(false, fixture.Transport.Requests.Where(r => r.Uri.AbsolutePath != "/_api/marked"));
        ordinary.RestClient.Client.DefaultRequestHeaders.UserAgent.ToString().Should().Be(fixture.DefaultUserAgent);
        marked.Dispose();
        await ordinary.Web.GetAsync(value => value.Id);
        fixture.AssertHeaders(false, fixture.Transport.Requests.TakeLast(1));
    }

    [Fact]
    public async Task Configured_sdk_identification_is_preserved_without_a_version_substitute()
    {
        using var fixture = new DiscoveryTransportFixture("NONISV|SharePointPnP|PnPCoreSDK/custom-build extra-product/2.0");
        using var factory = fixture.CreateFactory(true);
        await factory.GetAsync(DiscoveryTransportFixture.WebUrl);
        fixture.AssertHeaders(true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Site_and_web_enumeration_only_opt_in_for_classic_page_discovery(bool pages, bool enabled)
    {
        using var fixture = new DiscoveryTransportFixture();
        var auth = SiteEnumerationManager.DiscoveryAuthentication(fixture.Authentication,
            new ClassicOptions { Pages = pages, DiscoveryTestTraffic = enabled });
        using var context = await fixture.ContextFactory.CreateAsync(DiscoveryTransportFixture.WebUrl, auth);
        await context.Web.GetAsync(web => web.Id, web => web.ServerRelativeUrl);
        fixture.AssertHeaders(pages && enabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Modeled_batches_and_cloned_discovery_contexts_preserve_the_policy(bool? enabled)
    {
        using var fixture = new DiscoveryTransportFixture();
        using var factory = fixture.CreateFactory(enabled);
        using var context = await factory.CreateContextAsync(DiscoveryTransportFixture.WebUrl, CancellationToken.None);
        using var clone = await context.CloneAsync();
        fixture.Transport.Requests.Clear();
        await clone.Site.LoadBatchAsync(null, site => site.Id);
        await clone.Web.LoadBatchAsync(null, web => web.WelcomePage);
        await clone.ExecuteAsync();
        clone.Web.WelcomePage.Should().Be("default.aspx");
        fixture.Transport.Requests.Should().ContainSingle();
        fixture.Transport.Requests.Single().Method.Should().Be("POST");
        fixture.Transport.Requests.Single().Uri.AbsolutePath.Should().EndWith("/$batch");
        fixture.AssertHeaders(enabled ?? false);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_provider_pagination_marks_every_continuation_request(bool? enabled)
    {
        using var fixture = new DiscoveryTransportFixture();
        using var factory = fixture.CreateFactory(enabled);
        await factory.GetAsync(DiscoveryTransportFixture.WebUrl);
        fixture.Transport.Requests.Clear();
        var next = DiscoveryTransportFixture.WebUrl.AbsoluteUri + "/_api/web/lists?$skiptoken=next";
        fixture.Transport.Respond = request => DiscoveryTransportFixture.RecordingTransport.Json(
            request.RequestUri.Query.Contains("$skiptoken") ? "{\"value\":[]}" :
            "{\"value\":[],\"@odata.nextLink\":\"" + next + "\"}");
        using var provider = new SharePointLiveAspxDiscoveryProvider(new("fixture", "fixture", "fixture/v1", "fixture"),
            factory, new AspxWebAcquisitionContext(Guid.Parse(DiscoveryTransportFixture.SiteId), DiscoveryTransportFixture.WebUrl,
                Guid.Parse(DiscoveryTransportFixture.WebId), DiscoveryTransportFixture.WebUrl, "/sites/discovery", "STS#3"));
        await provider.EnumerateChildrenAsync(provider.RootScope);
        fixture.Transport.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/lists")).Should().HaveCount(2);
        fixture.Transport.Requests.Last().Uri.AbsoluteUri.Should().Be(next);
        provider.ReferenceCollector.ReadPaginationEvidence().Should().HaveCount(2);
        fixture.AssertHeaders(enabled ?? false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_and_errors_keep_headers_and_discovery_error_semantics(bool enabled)
    {
        using var fixture = new DiscoveryTransportFixture();
        using var factory = fixture.CreateFactory(enabled);
        var client = await factory.GetAsync(DiscoveryTransportFixture.WebUrl);
        fixture.Transport.Requests.Clear();
        var attempts = 0;
        fixture.Transport.Respond = request =>
        {
            var response = DiscoveryTransportFixture.RecordingTransport.Json("{\"value\":[]}",
                ++attempts == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("Retry-After", "0");
            return response;
        };
        var page = await client.GetPageAsync(new Uri(DiscoveryTransportFixture.WebUrl, "/_api/web/lists"));
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        attempts.Should().Be(2);
        fixture.AssertHeaders(enabled);

        fixture.Transport.Requests.Clear();
        fixture.Transport.Respond = _ => DiscoveryTransportFixture.RecordingTransport.Json(
            "{\"error\":{\"code\":\"AccessDenied\",\"message\":{\"lang\":\"en-US\",\"value\":\"Access denied\"}}}", HttpStatusCode.Forbidden);
        var denied = await client.GetPageAsync(new Uri(DiscoveryTransportFixture.WebUrl, "/_api/web/lists"));
        denied.Outcome.Should().Be(DiscoveryTerminalOutcome.Denied);
        // The existing modeled-read classifier sees the SDK's generic exception.Message,
        // not its Error.HttpResponseCode. Preserve its Failed state and reason here.
        var welcomeFailure = await client.ReadWelcomePageAsync();
        welcomeFailure.Outcome.Should().Be(DiscoveryTerminalOutcome.Failed);
        welcomeFailure.ErrorCode.Should().Be("welcome_page_failed");
        var fileFailure = await client.ResolveFileAsync("/sites/discovery/default.aspx");
        fileFailure.Outcome.Should().Be(DiscoveryTerminalOutcome.Failed);
        fileFailure.ErrorCode.Should().Be("locator_resolution_failed");
        var folderFailure = await client.ReadFolderAsync("/sites/discovery");
        folderFailure.Outcome.Should().Be(DiscoveryTerminalOutcome.Failed);
        folderFailure.ErrorCode.Should().Be("web_root_folder_failed");
        fixture.AssertHeaders(enabled);

        fixture.Transport.Requests.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var direct = () => client.GetPageAsync(DiscoveryTransportFixture.WebUrl, cancellation.Token);
        var welcome = () => client.ReadWelcomePageAsync(cancellation.Token);
        var file = () => client.ResolveFileAsync("/sites/discovery/default.aspx", cancellation.Token);
        var folder = () => client.ReadFolderAsync("/sites/discovery", cancellation.Token);
        await direct.Should().ThrowAsync<OperationCanceledException>();
        await welcome.Should().ThrowAsync<OperationCanceledException>();
        await file.Should().ThrowAsync<OperationCanceledException>();
        await folder.Should().ThrowAsync<OperationCanceledException>();
        fixture.Transport.Requests.Should().BeEmpty();
    }
}
