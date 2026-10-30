using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

/// <summary>Exercises bounded physical Files cursors and durable attempts without an authenticated SharePoint connection.</summary>
[Trait("Category", "ClassicDiscoveryIntegration")]
public sealed class SharePointFolderFilePagingTests
{
    private static readonly Uri Endpoint = new("https://contoso.sharepoint.com/sites/a/_api/web/GetFolderByServerRelativePath(decodedUrl=@u)/Files?@u='%2Fsites%2Fa%2FPages'&$select=UniqueId,Name,ServerRelativeUrl&$expand=Properties");

    /// <summary>A capped response without nextLink is not EOF, including a short second page before the explicit empty probe.</summary>
    [Fact]
    public async Task Missing_next_link_reads_1000_then_111_then_empty()
    {
        using var client = new PagingClient((uri, _) => Page(uri, Rows(Number(uri, "$skip") + 1,
            Number(uri, "$skip") == 0 ? 1000 : Number(uri, "$skip") == 1000 ? 111 : 0)));
        var reader = new SharePointFolderFilePageReader(client, Endpoint);

        var first = await reader.ReadAsync();
        var second = await reader.ReadAsync();
        var last = await reader.ReadAsync();

        first.Items.Should().HaveCount(1000);
        second.Items.Should().HaveCount(111);
        first.NextLink.Should().NotBeNull();
        second.NextLink.Should().NotBeNull();
        last.Items.Should().BeEmpty();
        last.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        reader.NextEndpoint.Should().BeNull();
        client.Requests.Select(uri => Number(uri, "$skip")).Should().Equal(0, 1000, 1111);
        client.Requests.Select(uri => Number(uri, "$top")).Should().Equal(1000, 1000, 1000);
        client.Requests.Should().OnlyContain(uri => Query(uri, "$orderby") == "Name asc" &&
            Query(uri, "$select") == "UniqueId,Name,ServerRelativeUrl" && Query(uri, "$expand") == "Properties" &&
            Query(uri, "@u") == "'/sites/a/Pages'");
        reader.Attempts.Should().HaveCount(3);
    }

    /// <summary>Size failures reduce the same cursor only, and the successful minimum size remains in force for the rest of the folder.</summary>
    [Fact]
    public async Task Size_backoff_preserves_offset_and_keeps_reduced_size()
    {
        using var client = new PagingClient((uri, _) => Number(uri, "$skip") == 1000 && Number(uri, "$top") > 200
            ? SizeFailure(uri)
            : Page(uri, Number(uri, "$skip") == 0 ? Rows(1, 1000)
                : Number(uri, "$skip") == 1000 ? Rows(1001, 200)
                : Number(uri, "$skip") == 1200 ? Rows(1201, 11) : Array.Empty<JsonElement>()));
        var reader = new SharePointFolderFilePageReader(client, Endpoint);

        var first = await reader.ReadAsync();
        var reduced = await reader.ReadAsync();
        var tail = await reader.ReadAsync();
        var last = await reader.ReadAsync();

        first.Items.Concat(reduced.Items).Concat(tail.Items).Select(row => row.GetProperty("UniqueId").GetString())
            .Should().HaveCount(1211).And.OnlyHaveUniqueItems();
        last.Items.Should().BeEmpty();
        client.Requests.Select(uri => Number(uri, "$skip")).Should().Equal(0, 1000, 1000, 1000, 1200, 1211);
        client.Requests.Select(uri => Number(uri, "$top")).Should().Equal(1000, 1000, 500, 200, 200, 200);
        reader.RequestedPageSize.Should().Be(200);
        reader.Attempts.Select(page => page.ErrorCode).Should().Equal(null, "json_response_size_limit",
            "json_response_size_limit", null, null, null);
        reduced.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        reader.NextEndpoint.Should().BeNull();
    }

    /// <summary>The minimum-size failure terminates the current chain without erasing any previously returned physical observations.</summary>
    [Fact]
    public async Task Minimum_size_failure_keeps_prior_facts_and_stops()
    {
        using var client = new PagingClient((uri, _) => Number(uri, "$skip") == 0
            ? Page(uri, Rows(1, 3)) : SizeFailure(uri));
        var reader = new SharePointFolderFilePageReader(client, Endpoint);

        var retained = await reader.ReadAsync();
        var failure = await reader.ReadAsync();

        retained.Items.Should().HaveCount(3);
        failure.Outcome.Should().Be(DiscoveryTerminalOutcome.Truncated);
        failure.ErrorCode.Should().Be("json_response_size_limit");
        reader.NextEndpoint.Should().BeNull();
        client.Requests.Select(uri => Number(uri, "$skip")).Should().Equal(0, 3, 3, 3);
        client.Requests.Select(uri => Number(uri, "$top")).Should().Equal(1000, 1000, 500, 200);
        reader.Attempts.Should().HaveCount(4);
        var action = () => reader.ReadAsync();
        await action.Should().ThrowAsync<InvalidOperationException>();
        client.Requests.Should().HaveCount(4);
    }

    /// <summary>Partial HTTP, malformed JSON, denied, throttle and timeout outcomes are not evidence of an oversized response.</summary>
    [Theory]
    [InlineData(HttpStatusCode.PartialContent, DiscoveryTerminalOutcome.Truncated, "partial_content")]
    [InlineData(HttpStatusCode.OK, DiscoveryTerminalOutcome.Unknown, "invalid_json")]
    [InlineData(HttpStatusCode.Forbidden, DiscoveryTerminalOutcome.Denied, "access_denied")]
    [InlineData(HttpStatusCode.TooManyRequests, DiscoveryTerminalOutcome.Failed, "http_429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, DiscoveryTerminalOutcome.Failed, "http_503")]
    [InlineData(HttpStatusCode.RequestTimeout, DiscoveryTerminalOutcome.Failed, "request_timeout")]
    [InlineData(HttpStatusCode.OK, DiscoveryTerminalOutcome.Failed, "json_response_size_limit")]
    public async Task Non_size_failures_never_reduce_or_retry(HttpStatusCode status, DiscoveryTerminalOutcome outcome, string error)
    {
        using var client = new PagingClient((uri, _) => Page(uri, Array.Empty<JsonElement>(), outcome, error, status));
        var reader = new SharePointFolderFilePageReader(client, Endpoint);

        var page = await reader.ReadAsync();

        page.Outcome.Should().Be(outcome);
        page.ErrorCode.Should().Be(error);
        reader.RequestedPageSize.Should().Be(1000);
        reader.NextEndpoint.Should().BeNull();
        client.Requests.Should().ContainSingle();
        reader.Attempts.Should().ContainSingle();
    }

    /// <summary>A server that ignores top is retried at the same offset, then stopped at the minimum with oversized items discarded.</summary>
    [Fact]
    public async Task Ignored_top_cannot_escape_the_item_limit()
    {
        using var client = new PagingClient((uri, _) => Page(uri, Rows(1, Number(uri, "$top") + 1)));
        var reader = new SharePointFolderFilePageReader(client, Endpoint);

        var page = await reader.ReadAsync();

        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Truncated);
        page.ErrorCode.Should().Be("file_page_item_limit_exceeded");
        page.Items.Should().BeEmpty();
        reader.NextEndpoint.Should().BeNull();
        client.Requests.Select(uri => Number(uri, "$top")).Should().Equal(1000, 500, 200);
        client.Requests.Select(uri => Number(uri, "$skip")).Should().Equal(0, 0, 0);
        reader.Attempts.Select(attempt => attempt.Items.Count).Should().Equal(1001, 501, 201);
    }

    /// <summary>Successful recovery from an ignored top retains only the resolved page and does not advance by discarded rows.</summary>
    [Fact]
    public async Task Oversized_item_retry_does_not_skip_discarded_rows()
    {
        using var client = new PagingClient((uri, _) => Page(uri, Number(uri, "$skip") == 0
            ? Rows(1, Number(uri, "$top") == 1000 ? 1001 : 7) : Array.Empty<JsonElement>()));
        var reader = new SharePointFolderFilePageReader(client, Endpoint);

        var recovered = await reader.ReadAsync();
        await reader.ReadAsync();

        recovered.Items.Should().HaveCount(7);
        recovered.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        client.Requests.Select(uri => Number(uri, "$skip")).Should().Equal(0, 0, 7);
        client.Requests.Select(uri => Number(uri, "$top")).Should().Equal(1000, 500, 500);
    }

    /// <summary>Repeated real File GUIDs within one response or across cursors keep facts but never authorize completion.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_GUID_or_ignored_skip_is_incomplete(bool withinPage)
    {
        using var client = new PagingClient((uri, _) => Page(uri,
            withinPage ? Rows(1, 1).Concat(Rows(1, 1)).ToArray() : Rows(1, 1)));
        var reader = new SharePointFolderFilePageReader(client, Endpoint);

        var page = await reader.ReadAsync();
        if (!withinPage)
        {
            page = await reader.ReadAsync();
        }

        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Unknown);
        page.ErrorCode.Should().Be("file_page_duplicate_identity");
        page.Items.Should().NotBeEmpty();
        reader.NextEndpoint.Should().BeNull();
        client.Requests.Should().HaveCount(withinPage ? 1 : 2);
    }

    /// <summary>Foreign hosts, foreign paths, changed projections and order drift are rejected before any continuation is followed.</summary>
    [Theory]
    [InlineData("foreign-host")]
    [InlineData("foreign-path")]
    [InlineData("projection")]
    [InlineData("order")]
    public async Task Untrusted_next_link_is_not_followed(string change)
    {
        var next = change switch
        {
            "foreign-host" => Endpoint.AbsoluteUri.Replace("contoso.sharepoint.com", "evil.example"),
            "foreign-path" => Endpoint.AbsoluteUri.Replace("/Files?", "/Folders?"),
            "projection" => Endpoint.AbsoluteUri.Replace("UniqueId,Name,ServerRelativeUrl", "UniqueId"),
            _ => Endpoint.AbsoluteUri + "&$orderby=Name%20desc",
        };
        using var client = new PagingClient((uri, _) => Page(uri, Rows(1, 1), next: next));
        var reader = new SharePointFolderFilePageReader(client, Endpoint);

        var page = await reader.ReadAsync();

        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Unknown);
        page.ErrorCode.Should().Be("file_page_continuation_invalid");
        page.Items.Should().ContainSingle();
        reader.NextEndpoint.Should().BeNull();
        client.Requests.Should().ContainSingle();
    }

    /// <summary>One repeated server cursor stops the chain even when the response happens to contain new GUIDs.</summary>
    [Fact]
    public async Task Repeated_server_token_cannot_claim_progress()
    {
        using var client = new PagingClient((uri, call) => Page(uri, Rows(call, 1),
            next: Endpoint.AbsoluteUri + "&$skiptoken=repeat"));
        var reader = new SharePointFolderFilePageReader(client, Endpoint);

        await reader.ReadAsync();
        var last = await reader.ReadAsync();

        last.Outcome.Should().Be(DiscoveryTerminalOutcome.Unknown);
        last.ErrorCode.Should().Be("file_page_continuation_invalid");
        reader.NextEndpoint.Should().BeNull();
        client.Requests.Should().HaveCount(2);
    }

    /// <summary>A valid server cursor is preserved through size backoff instead of inventing an offset that could skip files.</summary>
    [Fact]
    public async Task Server_cursor_backoff_preserves_token_and_reduced_size()
    {
        using var client = new PagingClient((uri, _) => Query(uri, "$skiptoken") == null
            ? Page(uri, Rows(1, 1), next: Endpoint.AbsoluteUri + "&$skiptoken=cursor-A")
            : Number(uri, "$top") == 1000 ? SizeFailure(uri)
            : Page(uri, Array.Empty<JsonElement>()));
        var reader = new SharePointFolderFilePageReader(client, Endpoint);

        await reader.ReadAsync();
        var last = await reader.ReadAsync();

        last.Items.Should().BeEmpty();
        last.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        client.Requests.Select(uri => Query(uri, "$skiptoken")).Should().Equal(null, "cursor-A", "cursor-A");
        client.Requests.Select(uri => Number(uri, "$top")).Should().Equal(1000, 1000, 500);
        reader.NextEndpoint.Should().BeNull();
    }

    /// <summary>Cancellation after a completed attempt retains its evidence and retries the same cursor rather than advancing unseen facts.</summary>
    [Fact]
    public async Task Cancellation_retains_attempt_and_does_not_advance_cursor()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new PagingClient((uri, call) =>
        {
            if (call == 2)
            {
                cancellation.Cancel();
            }
            return Page(uri, Rows(call == 1 ? 1 : 2, 1));
        });
        var reader = new SharePointFolderFilePageReader(client, Endpoint);
        var first = await reader.ReadAsync();
        var action = () => reader.ReadAsync(cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();

        reader.Attempts.Should().HaveCount(2);
        first.Items.Should().ContainSingle();
        Number(reader.NextEndpoint, "$skip").Should().Be(1);
        var retried = await reader.ReadAsync();
        retried.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        retried.Items.Single().GetProperty("UniqueId").GetString().Should().Be("cccccccc-cccc-cccc-cccc-000000000002");
        client.Requests.Select(uri => Number(uri, "$skip")).Should().Equal(0, 1, 1);
        Number(reader.NextEndpoint, "$skip").Should().Be(2);
    }

    /// <summary>An already-cancelled call emits no request and leaves the pending cursor and all completed attempts untouched.</summary>
    [Fact]
    public async Task Pre_cancelled_call_does_not_create_attempt_or_move_cursor()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new PagingClient((uri, _) => Page(uri, Array.Empty<JsonElement>()));
        var reader = new SharePointFolderFilePageReader(client, Endpoint);
        var pending = reader.NextEndpoint;

        var action = () => reader.ReadAsync(cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();

        reader.NextEndpoint.Should().Be(pending);
        reader.Attempts.Should().BeEmpty();
        client.Requests.Should().BeEmpty();
    }

    /// <summary>Missing GUID metadata remains a provider-owned gap, not an invented path identity or reader-level duplicate key.</summary>
    [Fact]
    public async Task Missing_GUID_is_retained_without_minting_identity()
    {
        var row = JsonSerializer.SerializeToElement(new { Name = "missing.aspx", ServerRelativeUrl = "/sites/a/Pages/missing.aspx" });
        using var client = new PagingClient((uri, _) => Page(uri, Number(uri, "$skip") == 0
            ? new[] { row } : Array.Empty<JsonElement>()));
        var reader = new SharePointFolderFilePageReader(client, Endpoint);

        var observed = await reader.ReadAsync();
        await reader.ReadAsync();

        observed.Items.Single().TryGetProperty("UniqueId", out _).Should().BeFalse();
        client.Requests.Select(uri => Number(uri, "$skip")).Should().Equal(0, 1);
        reader.NextEndpoint.Should().BeNull();
    }

    /// <summary>Builds deterministic physical identities independent of the requested cursor or any production protocol constants.</summary>
    private static JsonElement[] Rows(int first, int count)
    {
        return Enumerable.Range(first, count).Select(id => JsonSerializer.SerializeToElement(new
        {
            UniqueId = "cccccccc-cccc-cccc-cccc-" + id.ToString("D12", CultureInfo.InvariantCulture),
            Name = "page-" + id.ToString("D6", CultureInfo.InvariantCulture) + ".aspx",
            ServerRelativeUrl = "/sites/a/Pages/page-" + id.ToString("D6", CultureInfo.InvariantCulture) + ".aspx",
        })).ToArray();
    }

    /// <summary>Creates a raw transport observation so the helper, not the fixture, decides whether the collection has reached EOF.</summary>
    private static SharePointRestPage Page(Uri uri, IReadOnlyList<JsonElement> items,
        DiscoveryTerminalOutcome outcome = DiscoveryTerminalOutcome.Complete, string error = null,
        HttpStatusCode status = HttpStatusCode.OK, string next = null)
    {
        return new SharePointRestPage(uri, status, items, next, DiscoveryHash.Of(uri.AbsoluteUri, items.Count.ToString(CultureInfo.InvariantCulture)),
            "fixture", outcome, error, ReceivedAtUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>Simulates the reader's exact bounded-body classification without conflating partial HTTP or JSON syntax failures.</summary>
    private static SharePointRestPage SizeFailure(Uri uri)
    {
        return Page(uri, Array.Empty<JsonElement>(), DiscoveryTerminalOutcome.Truncated, "json_response_size_limit");
    }

    /// <summary>Parses a numeric cursor directly from the wire request rather than consulting the production paging helper.</summary>
    private static int Number(Uri uri, string key)
    {
        return int.Parse(Query(uri, key), CultureInfo.InvariantCulture);
    }

    /// <summary>Decodes one query value while preserving the original endpoint for separate projection-binding assertions.</summary>
    private static string Query(Uri uri, string key)
    {
        foreach (var segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = segment.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) == key)
            {
                return parts.Length == 2 ? WebUtility.UrlDecode(parts[1]) : string.Empty;
            }
        }
        return null;
    }

    private sealed class PagingClient : ISharePointAspxRestClient
    {
        private readonly Func<Uri, int, SharePointRestPage> response;
        internal List<Uri> Requests { get; } = new();
        public Uri WebUrl { get; } = new("https://contoso.sharepoint.com/sites/a");

        /// <summary>Binds a scenario-specific response function without allowing any modeled or live acquisition route.</summary>
        internal PagingClient(Func<Uri, int, SharePointRestPage> response)
        {
            this.response = response;
        }

        /// <summary>Records each actual invocation before returning the scenario's unmodified transport evidence.</summary>
        public Task<SharePointRestPage> GetPageAsync(Uri requestUri, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(requestUri);
            return Task.FromResult(response(requestUri, Requests.Count));
        }

        /// <summary>Fails if physical collection paging unexpectedly resolves an individual file.</summary>
        public Task<SharePointResolvedFile> ResolveFileAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Paging must not resolve a file.");
        }

        /// <summary>Fails if physical collection paging unexpectedly asks for welcome-page metadata.</summary>
        public Task<SharePointModeledValue> ReadWelcomePageAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Paging must not read welcome metadata.");
        }

        /// <summary>Fails if the bounded reader attempts to bypass its transport with an expanded SDK folder.</summary>
        public Task<SharePointModeledFolderResult> ReadFolderAsync(string serverRelativeUrl, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Paging must not use modeled folder children.");
        }

        /// <summary>Releases no external resources because all recorded observations are in-memory fixtures.</summary>
        public void Dispose()
        {
        }
    }
}
