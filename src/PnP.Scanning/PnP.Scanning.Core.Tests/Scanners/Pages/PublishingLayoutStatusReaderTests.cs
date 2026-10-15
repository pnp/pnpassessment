using FluentAssertions;
using PnP.Scanning.Core.Scanners;
using PnP.Scanning.Core.Storage;
using Xunit;

namespace PnP.Scanning.Core.Tests.Scanners.Pages;

public class PublishingLayoutStatusReaderTests
{
    private static readonly Uri WebUrl = new("https://contoso.sharepoint.com/sites/a/subweb");
    private const string LayoutUrl = "/sites/a/_catalogs/masterpage/Basic.aspx";

    [Fact]
    public async Task Shared_layout_is_read_once_for_absolute_and_server_relative_references()
    {
        var requests = new List<string>();
        var reader = new PublishingLayoutStatusReader(WebUrl, url =>
        {
            requests.Add(url);
            return Task.FromResult("1");
        });
        var first = Page("https://contoso.sharepoint.com" + LayoutUrl);
        var second = Page(LayoutUrl);
        first.CustomizedPageStatus = "Customized";

        await reader.EnrichAsync(first);
        await reader.EnrichAsync(second);

        requests.Should().Equal(LayoutUrl);
        first.LayoutCustomizedPageStatus.Should().Be("Uncustomized");
        second.LayoutCustomizedPageStatus.Should().Be("Uncustomized");
        first.CustomizedPageStatus.Should().Be("Customized", "page and layout are separate files");
        first.LayoutUrl.Should().Be("https://contoso.sharepoint.com" + LayoutUrl);
    }

    [Theory]
    [InlineData("WikiPage")]
    [InlineData("WebPartPage")]
    [InlineData("ASPXPage")]
    [InlineData("BlogPage")]
    [InlineData("ModernPage")]
    public async Task Non_publishing_pages_have_null_layout_metadata_without_a_request(string pageType)
    {
        var reader = new PublishingLayoutStatusReader(WebUrl, _ => throw new InvalidOperationException("Unexpected request"));
        var page = Page(LayoutUrl);
        page.PageType = pageType;
        page.LayoutCustomizedPageStatus = "Uncustomized";

        await reader.EnrichAsync(page);

        page.LayoutUrl.Should().BeNull();
        page.LayoutCustomizedPageStatus.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Missing_layout_reference_does_not_infer_a_status(string url)
    {
        var reader = new PublishingLayoutStatusReader(WebUrl, _ => throw new InvalidOperationException("Unexpected request"));
        var page = Page(url);
        await reader.EnrichAsync(page);
        page.LayoutCustomizedPageStatus.Should().BeNull();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("None", "None")]
    [InlineData("2", "Customized")]
    public async Task Layout_status_preserves_unknown_none_and_customized(string returned, string expected)
    {
        var reader = new PublishingLayoutStatusReader(WebUrl, _ => Task.FromResult(returned));
        var page = Page(LayoutUrl);
        await reader.EnrichAsync(page);
        page.LayoutCustomizedPageStatus.Should().Be(expected);
    }

    [Theory]
    [InlineData("https://other.sharepoint.com/sites/a/_catalogs/masterpage/Basic.aspx")]
    [InlineData("//other.sharepoint.com/Basic.aspx")]
    [InlineData("Basic.aspx")]
    public async Task Invalid_layout_url_surfaces_an_error_instead_of_guessing(string url)
    {
        var reader = new PublishingLayoutStatusReader(WebUrl, _ => throw new InvalidOperationException("Unexpected request"));
        var page = Page(url);
        var enrich = () => reader.EnrichAsync(page);
        await enrich.Should().ThrowAsync<InvalidDataException>();
        page.LayoutCustomizedPageStatus.Should().BeNull();
    }

    [Fact]
    public async Task Failed_shared_layout_read_is_cached_and_reported_for_each_page()
    {
        var requests = 0;
        var reader = new PublishingLayoutStatusReader(WebUrl, _ =>
        {
            requests++;
            throw new UnauthorizedAccessException("Layout denied");
        });
        foreach (var page in new[] { Page(LayoutUrl), Page(LayoutUrl) })
        {
            page.WebPartCount = 3;
            var enrich = () => reader.EnrichAsync(page);
            await enrich.Should().ThrowAsync<UnauthorizedAccessException>();
            page.LayoutCustomizedPageStatus.Should().BeNull();
            page.WebPartCount.Should().Be(3);
        }
        requests.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_and_unrecognized_status_are_not_reported_as_success()
    {
        var cancelled = new PublishingLayoutStatusReader(WebUrl, _ => throw new OperationCanceledException());
        var cancel = () => cancelled.EnrichAsync(Page(LayoutUrl));
        await cancel.Should().ThrowAsync<OperationCanceledException>();

        var invalid = new PublishingLayoutStatusReader(WebUrl, _ => Task.FromResult("99"));
        var read = () => invalid.EnrichAsync(Page(LayoutUrl));
        await read.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Different_layouts_and_reader_instances_do_not_share_results()
    {
        var requests = 0;
        Task<string> Read(string url)
        {
            requests++;
            return Task.FromResult(url == LayoutUrl ? "Uncustomized" : "Customized");
        }
        var first = new PublishingLayoutStatusReader(WebUrl, Read);
        var second = new PublishingLayoutStatusReader(WebUrl, Read);
        var custom = Page("/sites/a/_catalogs/masterpage/Custom.aspx");
        await first.EnrichAsync(Page(LayoutUrl));
        await first.EnrichAsync(custom);
        await second.EnrichAsync(Page(LayoutUrl));
        requests.Should().Be(3);
        custom.LayoutCustomizedPageStatus.Should().Be("Customized");
    }

    private static ClassicPage Page(string url) => new()
    {
        PageType = PageScanComponent.PublishingPage,
        LayoutUrl = url,
    };
}
