using FluentAssertions;
using PnP.Scanning.Core.Discovery;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery;

/// <summary>Exercises the real bounded JSON transport with in-memory HTTP handlers and a monotonic virtual clock only.</summary>
public sealed class SharePointAspxRestTransportTests
{
    private const string WebUrl = "https://contoso.sharepoint.com/sites/reuse";
    private const int DefaultMaxBytes = 4 * 1024 * 1024;
    private static readonly Uri Web = new(WebUrl);
    private static readonly Uri RequestUri = new(WebUrl + "/_api/Web/Lists?$select=Id");
    private static readonly DateTimeOffset Epoch = new(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Reauthenticates fresh GETs after disposing the throttled response and request, then records only final successful receipt metadata.</summary>
    [Fact]
    public async Task Retry_429_seconds_then_200_disposes_before_delay_and_preserves_final_receipt()
    {
        using var fixture = new TransportFixture();
        var first = fixture.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: "2");
        first.Headers.Add("SPRequestGuid", "first-request");
        var last = fixture.Enqueue(HttpStatusCode.OK);
        last.Headers.Add("SPRequestGuid", "final-request");
        last.Headers.Add("x-ms-correlation-id", "final-correlation");
        var requestStreams = new List<TrackingStream>();
        fixture.Authenticate = (request, _) =>
        {
            var stream = new TrackingStream(Array.Empty<byte>());
            requestStreams.Add(stream);
            request.Content = new StreamContent(stream);
        };
        fixture.DelayOverride = (duration, token) =>
        {
            token.ThrowIfCancellationRequested();
            first.Disposed.Should().BeTrue();
            fixture.Streams[0].Disposed.Should().BeTrue();
            requestStreams[0].Disposed.Should().BeTrue();
            fixture.Clock.Advance(duration);
            return Task.CompletedTask;
        };

        var page = await fixture.ReadAsync();

        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.AttemptCount.Should().Be(2);
        page.AttemptLimit.Should().Be(3);
        page.ErrorCode.Should().BeNull();
        page.ReceivedAtUtc.Should().Be(Epoch.AddSeconds(2));
        page.RequestId.Should().Be("final-request");
        page.CorrelationId.Should().Be("final-correlation");
        page.Items.Should().ContainSingle();
        fixture.Delays.Should().Equal(TimeSpan.FromSeconds(2));
        fixture.Handler.SendTimes.Should().Equal(TimeSpan.Zero, TimeSpan.FromSeconds(2));
        fixture.Handler.Requests.Should().HaveCount(2);
        fixture.Handler.Requests[0].Should().NotBeSameAs(fixture.Handler.Requests[1]);
        fixture.Authenticated.Should().Equal(fixture.Handler.Requests);
        fixture.Handler.Accepts.Should().OnlyContain(value => value == "application/json; odata=nometadata");
        fixture.Handler.Requests.Should().OnlyContain(request => request.RequestUri == RequestUri && request.Method == HttpMethod.Get);
        fixture.Responses.Should().OnlyContain(response => response.Disposed);
        fixture.Streams.Should().OnlyContain(stream => stream.Disposed);
        fixture.Streams[0].ReadBytes.Should().Be(0, "throttled bodies are discarded without acquisition");
        requestStreams.Should().OnlyContain(stream => stream.Disposed);
    }

    /// <summary>Honors a 503 HTTP-date rather than substituting a shorter fallback delay.</summary>
    [Fact]
    public async Task Retry_503_http_date_then_200_waits_until_server_date()
    {
        using var fixture = new TransportFixture();
        fixture.Enqueue(HttpStatusCode.ServiceUnavailable, retryAfter: Epoch.AddSeconds(7).ToString("r"));
        fixture.Enqueue(HttpStatusCode.OK);

        var page = await fixture.ReadAsync();

        fixture.Delays.Should().Equal(TimeSpan.FromSeconds(7));
        fixture.Handler.SendTimes.Should().Equal(TimeSpan.Zero, TimeSpan.FromSeconds(7));
        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        page.AttemptCount.Should().Be(2);
        page.ReceivedAtUtc.Should().Be(Epoch.AddSeconds(7));
    }

    /// <summary>Bounds repeated throttle responses by the attempt limit and preserves the last status, headers, and receipt time.</summary>
    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Exhaustion_returns_explicit_final_throttle_failure(int status)
    {
        using var fixture = new TransportFixture();
        fixture.Enqueue((HttpStatusCode)status);
        fixture.Enqueue((HttpStatusCode)status);
        var last = fixture.Enqueue((HttpStatusCode)status);
        last.Headers.Add("SPRequestGuid", "exhausted-request");

        var page = await fixture.ReadAsync();

        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Failed);
        page.StatusCode.Should().Be((HttpStatusCode)status);
        page.ErrorCode.Should().Be("json_throttle_attempts_exhausted");
        page.AttemptCount.Should().Be(3);
        page.AttemptLimit.Should().Be(3);
        page.ReceivedAtUtc.Should().Be(Epoch.AddSeconds(3));
        page.RequestId.Should().Be("exhausted-request");
        page.Items.Should().BeEmpty();
        page.NextLink.Should().BeNull();
        fixture.Handler.Requests.Should().HaveCount(3);
        fixture.Delays.Should().Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        fixture.Responses.Should().OnlyContain(response => response.Disposed);
        fixture.Streams.Should().OnlyContain(stream => stream.Disposed && stream.ReadBytes == 0);
    }

    /// <summary>Reports an explicitly smaller attempt limit without waiting or issuing an extra throttled request.</summary>
    [Fact]
    public async Task Single_attempt_policy_reports_limit_without_retrying()
    {
        using var fixture = new TransportFixture(attemptLimit: 1);
        fixture.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: "1");

        var page = await fixture.ReadAsync();

        page.AttemptCount.Should().Be(1);
        page.AttemptLimit.Should().Be(1);
        page.ErrorCode.Should().Be("json_throttle_attempts_exhausted");
        fixture.Delays.Should().BeEmpty();
        fixture.Handler.Requests.Should().ContainSingle();
    }

    /// <summary>Rejects retry configurations that would remove bounded attempts or exceed the small safety cap.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Invalid_attempt_limit_is_rejected(int attemptLimit)
    {
        Action create = () => new SharePointRestThrottlePolicy(attemptLimit);
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Rejects exact-budget, oversized, malformed, or unrepresentable server delays instead of clamping and retrying early.</summary>
    [Theory]
    [InlineData("20")]
    [InlineData("21")]
    [InlineData("999999999999999999999999999999")]
    [InlineData("invalid-retry-after")]
    public async Task Server_delay_outside_budget_never_waits_or_retries_early(string retryAfter)
    {
        using var fixture = new TransportFixture();
        fixture.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: retryAfter);

        var page = await fixture.ReadAsync();

        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Failed);
        page.ErrorCode.Should().Be("json_throttle_delay_exceeds_budget");
        page.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        page.AttemptCount.Should().Be(1);
        page.AttemptLimit.Should().Be(3);
        page.ReceivedAtUtc.Should().Be(Epoch);
        fixture.Delays.Should().BeEmpty();
        fixture.Handler.Requests.Should().ContainSingle();
        fixture.Responses.Single().Disposed.Should().BeTrue();
        fixture.Streams.Single().Disposed.Should().BeTrue();
    }

    /// <summary>Rejects an HTTP-date beyond the overall deadline without reducing the requested wait.</summary>
    [Fact]
    public async Task Future_http_date_beyond_budget_is_an_immediate_explicit_failure()
    {
        using var fixture = new TransportFixture();
        fixture.Enqueue(HttpStatusCode.ServiceUnavailable, retryAfter: Epoch.AddMinutes(2).ToString("r"));

        var page = await fixture.ReadAsync();

        page.ErrorCode.Should().Be("json_throttle_delay_exceeds_budget");
        page.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        fixture.Delays.Should().BeEmpty();
        fixture.Handler.Requests.Should().ContainSingle();
    }

    /// <summary>Includes earlier HTTP time and backoff in the same budget rather than starting a new deadline on each attempt.</summary>
    [Fact]
    public async Task Accumulated_elapsed_budget_stops_a_later_throttle_without_shortening_delay()
    {
        using var fixture = new TransportFixture();
        fixture.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: "3");
        fixture.Enqueue(HttpStatusCode.ServiceUnavailable, retryAfter: "2");
        fixture.Handler.BeforeSend = index => fixture.Clock.Advance(TimeSpan.FromSeconds(index == 1 ? 4 : 2));

        var page = await fixture.ReadAsync(timeout: TimeSpan.FromSeconds(10));

        page.ErrorCode.Should().Be("json_throttle_delay_exceeds_budget");
        page.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        page.AttemptCount.Should().Be(2);
        page.ReceivedAtUtc.Should().Be(Epoch.AddSeconds(9));
        fixture.Delays.Should().Equal(TimeSpan.FromSeconds(3));
        fixture.Handler.Requests.Should().HaveCount(2);
        fixture.Responses.Should().OnlyContain(response => response.Disposed);
    }

    /// <summary>Checks monotonic elapsed time again after a delay overshoots, without issuing another request past the deadline.</summary>
    [Fact]
    public async Task Delay_overshoot_cannot_start_an_attempt_after_the_deadline()
    {
        using var fixture = new TransportFixture();
        fixture.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: "2");
        fixture.DelayOverride = (_, _) =>
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(20));
            return Task.CompletedTask;
        };

        var page = await fixture.ReadAsync();

        page.ErrorCode.Should().Be("json_response_timeout");
        page.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        page.AttemptCount.Should().Be(1);
        fixture.Handler.Requests.Should().ContainSingle();
    }

    /// <summary>Rechecks elapsed time when a delay implementation returns early so a server minimum remains a true minimum.</summary>
    [Fact]
    public async Task Early_delay_completion_waits_the_remaining_duration_before_retry()
    {
        using var fixture = new TransportFixture();
        fixture.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: "3");
        fixture.Enqueue(HttpStatusCode.OK);
        fixture.DelayOverride = (duration, _) =>
        {
            fixture.Clock.Advance(fixture.Delays.Count == 1 ? TimeSpan.FromSeconds(1) : duration);
            return Task.CompletedTask;
        };

        var page = await fixture.ReadAsync();

        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        fixture.Delays.Should().Equal(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
        fixture.Handler.SendTimes.Should().Equal(TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    /// <summary>Allows zero or already elapsed Retry-After values without imposing unnecessary fallback waits.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("Thu, 29 Oct 2026 00:00:00 GMT")]
    public async Task Nonpositive_server_wait_can_retry_immediately(string retryAfter)
    {
        using var fixture = new TransportFixture();
        fixture.Enqueue(HttpStatusCode.ServiceUnavailable, retryAfter: retryAfter);
        fixture.Enqueue(HttpStatusCode.OK);

        var page = await fixture.ReadAsync();

        page.AttemptCount.Should().Be(2);
        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        fixture.Delays.Should().BeEmpty();
    }

    /// <summary>Propagates caller cancellation from the retry wait even if the injected delay returns normally after cancellation.</summary>
    [Fact]
    public async Task Caller_cancellation_during_delay_propagates_and_never_retries()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new TransportFixture();
        var first = fixture.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: "1");
        fixture.DelayOverride = (_, token) =>
        {
            first.Disposed.Should().BeTrue();
            fixture.Streams.Single().Disposed.Should().BeTrue();
            token.CanBeCanceled.Should().BeTrue();
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        Func<Task> read = () => fixture.ReadAsync(cancellationToken: cancellation.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();

        fixture.Handler.Requests.Should().ContainSingle();
        fixture.Delays.Should().ContainSingle();
    }

    /// <summary>Does not authenticate or send any request for a caller token that was already cancelled.</summary>
    [Fact]
    public async Task Pre_cancelled_caller_token_propagates_before_authentication()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var fixture = new TransportFixture();

        Func<Task> read = () => fixture.ReadAsync(cancellationToken: cancellation.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();

        fixture.Authenticated.Should().BeEmpty();
        fixture.Handler.Requests.Should().BeEmpty();
        fixture.Delays.Should().BeEmpty();
    }

    /// <summary>Never retries denials, redirects, or unrelated HTTP failures even when Retry-After is present.</summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    public async Task Nonthrottle_status_is_a_single_attempt(int status)
    {
        using var fixture = new TransportFixture();
        var response = fixture.Enqueue((HttpStatusCode)status, retryAfter: "1");
        response.Headers.Location = new Uri("https://untrusted.example/redirect");

        var page = await fixture.ReadAsync();

        page.StatusCode.Should().Be((HttpStatusCode)status);
        page.AttemptCount.Should().Be(1);
        page.AttemptLimit.Should().Be(3);
        page.Outcome.Should().Be(status is 401 or 403 ? DiscoveryTerminalOutcome.Denied :
            status is >= 300 and <= 399 ? DiscoveryTerminalOutcome.Unknown : DiscoveryTerminalOutcome.Failed);
        fixture.Handler.Requests.Should().ContainSingle();
        fixture.Delays.Should().BeEmpty();
        response.Disposed.Should().BeTrue();
    }

    /// <summary>Rejects unsafe authenticated destinations before either authentication or transport is invoked.</summary>
    [Theory]
    [InlineData("http://contoso.sharepoint.com/sites/reuse/_api/Web")]
    [InlineData("https://other.example/sites/reuse/_api/Web")]
    [InlineData("https://contoso.sharepoint.com:444/sites/reuse/_api/Web")]
    [InlineData("https://contoso.sharepoint.com/sites/reuse/_api/Web#fragment")]
    [InlineData("https://user@contoso.sharepoint.com/sites/reuse/_api/Web")]
    [InlineData("https://contoso.sharepoint.com/sites/other/_api/Web")]
    [InlineData("https://contoso.sharepoint.com/sites/reuse/default.aspx")]
    public async Task Unsafe_request_uri_cannot_receive_authentication_or_retry(string uri)
    {
        using var fixture = new TransportFixture();

        Func<Task> read = () => fixture.ReadAsync(uri: new Uri(uri));
        await read.Should().ThrowAsync<ArgumentException>();

        fixture.Authenticated.Should().BeEmpty();
        fixture.Handler.Requests.Should().BeEmpty();
        fixture.Delays.Should().BeEmpty();
    }

    /// <summary>Rejects response URI drift before treating even a 429 as retryable.</summary>
    [Theory]
    [InlineData("https://untrusted.example/sites/reuse/_api/Web")]
    [InlineData("https://contoso.sharepoint.com/sites/reuse/_api/Other")]
    [InlineData("/_api/relative-response")]
    public async Task Unsafe_throttle_response_uri_is_not_followed_or_retried(string responseUri)
    {
        using var fixture = new TransportFixture();
        var response = fixture.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: "1");
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, responseUri);

        var page = await fixture.ReadAsync();

        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Unknown);
        page.ErrorCode.Should().Be("json_redirect_or_response_uri_drift");
        page.AttemptCount.Should().Be(1);
        fixture.Handler.Requests.Should().ContainSingle();
        fixture.Delays.Should().BeEmpty();
        fixture.Streams.Single().ReadBytes.Should().Be(0);
        fixture.Streams.Single().Disposed.Should().BeTrue();
    }

    /// <summary>Maintains the default four-MiB declared-length boundary without reading any oversized body.</summary>
    [Fact]
    public async Task Declared_oversized_response_is_rejected_before_stream_read()
    {
        using var fixture = new TransportFixture();
        var response = fixture.Enqueue(HttpStatusCode.OK);
        response.Content.Headers.ContentLength = DefaultMaxBytes + 1;

        var page = await fixture.ReadAsync();

        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Truncated);
        page.ErrorCode.Should().Be("json_response_size_or_partial");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Streams.Single().ReadBytes.Should().Be(0);
        fixture.Streams.Single().Disposed.Should().BeTrue();
        fixture.Handler.Requests.Should().ContainSingle();
    }

    /// <summary>Reads at most budget plus one sentinel byte for an unknown-length response and never retries a truncated body.</summary>
    [Fact]
    public async Task Unknown_length_response_stops_at_byte_limit_and_disposes_stream()
    {
        using var fixture = new TransportFixture();
        fixture.Enqueue(HttpStatusCode.OK, new string('x', 100));
        fixture.Streams.Single().ExposeLength = false;

        var page = await fixture.ReadAsync(maxBytes: 32);

        page.ErrorCode.Should().Be("json_response_size_limit");
        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Truncated);
        page.AttemptCount.Should().Be(1);
        fixture.Streams.Single().ReadBytes.Should().Be(33);
        fixture.Streams.Single().Disposed.Should().BeTrue();
        fixture.Delays.Should().BeEmpty();
    }

    /// <summary>Preserves incomplete-body and HTTP partial-content rejection independently of retry support.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incomplete_or_partial_response_never_yields_page_items(bool partial)
    {
        using var fixture = new TransportFixture();
        var response = fixture.Enqueue(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
        if (!partial)
        {
            response.Content.Headers.ContentLength = 1000;
        }

        var page = await fixture.ReadAsync();

        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Truncated);
        page.ErrorCode.Should().Be(partial ? "json_response_size_or_partial" : "json_response_incomplete_body");
        page.Items.Should().BeEmpty();
        fixture.Streams.Single().Disposed.Should().BeTrue();
        fixture.Handler.Requests.Should().ContainSingle();
        fixture.Delays.Should().BeEmpty();
    }

    /// <summary>Retains the response status and attempt receipt when a body stream fails instead of retrying a broken stream.</summary>
    [Fact]
    public async Task Incomplete_stream_is_disposed_and_not_retried()
    {
        using var fixture = new TransportFixture();
        fixture.Enqueue(HttpStatusCode.OK);
        fixture.Streams.Single().ReadError = new IOException("offline stream interrupted");

        var page = await fixture.ReadAsync();

        page.ErrorCode.Should().Be("json_response_incomplete_stream");
        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Truncated);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.AttemptCount.Should().Be(1);
        fixture.Streams.Single().Disposed.Should().BeTrue();
        fixture.Delays.Should().BeEmpty();
    }

    /// <summary>Includes authentication in the total elapsed deadline and never sends after authentication consumed that budget.</summary>
    [Fact]
    public async Task Authentication_elapsed_deadline_stops_before_send()
    {
        using var fixture = new TransportFixture();
        fixture.Authenticate = (_, _) => fixture.Clock.Advance(TimeSpan.FromSeconds(20));

        var page = await fixture.ReadAsync();

        page.ErrorCode.Should().Be("json_response_timeout");
        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Truncated);
        page.AttemptCount.Should().Be(1);
        page.StatusCode.Should().Be((HttpStatusCode)0);
        page.ReceivedAtUtc.Should().Be(Epoch.AddSeconds(20), "a response-free failure records the time of failure");
        fixture.Handler.Requests.Should().BeEmpty();
    }

    /// <summary>Checks the same deadline while streaming so a response cannot become complete after the overall budget expired.</summary>
    [Fact]
    public async Task Body_elapsed_deadline_disposes_response_and_returns_timeout()
    {
        using var fixture = new TransportFixture();
        fixture.Enqueue(HttpStatusCode.OK);
        fixture.Streams.Single().BeforeRead = () => fixture.Clock.Advance(TimeSpan.FromSeconds(20));

        var page = await fixture.ReadAsync();

        page.ErrorCode.Should().Be("json_response_timeout");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Truncated);
        fixture.Streams.Single().Disposed.Should().BeTrue();
        fixture.Delays.Should().BeEmpty();
    }

    /// <summary>Does not retry transport errors or internal HTTP timeouts that are not explicit 429/503 responses.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_failure_or_internal_timeout_is_not_retried(bool timeout)
    {
        using var fixture = new TransportFixture();
        fixture.Handler.SendError = timeout
            ? new TaskCanceledException("offline HTTP timeout")
            : new HttpRequestException("offline transport error");

        var page = await fixture.ReadAsync();

        page.ErrorCode.Should().Be(timeout ? "json_response_timeout" : "json_transport_failure");
        page.AttemptCount.Should().Be(1);
        page.AttemptLimit.Should().Be(3);
        page.StatusCode.Should().Be((HttpStatusCode)0);
        fixture.Handler.Requests.Should().ContainSingle();
        fixture.Delays.Should().BeEmpty();
    }

    /// <summary>Exercises GetPageAsync through an offline SDK context and proves discovery disposes only its owned HTTP client and granted context.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetPage_retry_preserves_borrowed_transport_and_explicit_context_ownership(bool ownsContext)
    {
        using var sdk = new SharedDiscoveryContextTests.OfflineSdk();
        var context = await sdk.CreateAsync();
        using var fixture = new TransportFixture();
        fixture.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: "1");
        fixture.Enqueue(HttpStatusCode.OK);
        fixture.Enqueue(HttpStatusCode.OK);
        var client = new PnPContextSharePointAspxRestClient(context, ownsContext, fixture.Http, fixture.Policy);
        var owned = Field<HttpClient>(client, "ownedJsonTransport");

        var page = await client.GetPageAsync(RequestUri);
        client.Dispose();
        client.Dispose();

        page.Outcome.Should().Be(DiscoveryTerminalOutcome.Complete);
        page.AttemptCount.Should().Be(2);
        page.AttemptLimit.Should().Be(3);
        page.ReceivedAtUtc.Should().Be(Epoch.AddSeconds(1));
        fixture.Handler.Authorizations.Should().OnlyContain(value => !string.IsNullOrEmpty(value));
        fixture.Handler.Disposed.Should().BeFalse();
        Field<bool>(context, "disposed").Should().Be(ownsContext);
        Action useOwned = () => owned.CancelPendingRequests();
        useOwned.Should().Throw<ObjectDisposedException>();
        using var proofRequest = new HttpRequestMessage(HttpMethod.Get, RequestUri);
        using var proof = await fixture.Http.SendAsync(proofRequest);
        proof.StatusCode.Should().Be(HttpStatusCode.OK, "the explicitly borrowed client must remain usable");
        sdk.Transport.Unexpected.Should().BeEmpty();
    }

    /// <summary>Keeps production-owned redirect, cookie, and ambient-credential protections while adding retry policy.</summary>
    [Fact]
    public async Task Owned_transport_remains_nonredirecting_cookieless_and_without_default_credentials()
    {
        using var sdk = new SharedDiscoveryContextTests.OfflineSdk();
        var context = await sdk.CreateAsync();
        using var client = new PnPContextSharePointAspxRestClient(context, ownsContext: false);
        var owned = Field<HttpClient>(client, "ownedJsonTransport");
        var handler = Field<HttpMessageHandler>(owned, "_handler").Should().BeOfType<HttpClientHandler>().Which;

        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
        handler.UseDefaultCredentials.Should().BeFalse();
    }

    /// <summary>Reads explicit lifecycle and transport fields without invoking any network operation.</summary>
    private static T Field<T>(object instance, string name)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                return (T)field.GetValue(instance);
            }
        }
        throw new InvalidOperationException("Expected lifecycle field was not found: " + name);
    }

    /// <summary>Owns only fake transport resources and advances virtual time instead of sleeping or accessing the network.</summary>
    private sealed class TransportFixture : IDisposable
    {
        internal ManualClock Clock { get; } = new();
        internal QueueHandler Handler { get; }
        internal HttpClient Http { get; }
        internal SharePointRestThrottlePolicy Policy { get; }
        internal List<TimeSpan> Delays { get; } = new();
        internal List<HttpRequestMessage> Authenticated { get; } = new();
        internal List<TrackingResponse> Responses { get; } = new();
        internal List<TrackingStream> Streams { get; } = new();
        internal Action<HttpRequestMessage, CancellationToken> Authenticate { get; set; }
        internal Func<TimeSpan, CancellationToken, Task> DelayOverride { get; set; }

        /// <summary>Connects a scripted handler and deterministic delay to the real bounded-reader seam.</summary>
        internal TransportFixture(int attemptLimit = SharePointRestThrottlePolicy.DefaultAttemptLimit)
        {
            Handler = new QueueHandler(Clock);
            Http = new HttpClient(Handler);
            Policy = new SharePointRestThrottlePolicy(attemptLimit, Clock, DelayAsync);
        }

        /// <summary>Queues a disposable in-memory JSON response with optional untrusted Retry-After header evidence.</summary>
        internal TrackingResponse Enqueue(HttpStatusCode status, string body = "{\"value\":[{\"Id\":\"offline\"}]}", string retryAfter = null)
        {
            var stream = new TrackingStream(Encoding.UTF8.GetBytes(body));
            var response = new TrackingResponse(status) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (retryAfter != null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }
            Streams.Add(stream);
            Responses.Add(response);
            Handler.Responses.Enqueue(response);
            return response;
        }

        /// <summary>Authenticates only through an in-memory marker and passes all bounds directly to the production reader.</summary>
        internal Task<SharePointRestPage> ReadAsync(int maxBytes = DefaultMaxBytes, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default, Uri uri = null) =>
            PnPContextSharePointAspxRestClient.ReadBoundedPageAsync(Http, Web, uri ?? RequestUri,
                AuthenticateAsync, maxBytes, timeout, cancellationToken, Policy);

        /// <summary>Records per-attempt authentication and allows deterministic cancellation or authentication-time simulation.</summary>
        private Task AuthenticateAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Authenticated.Add(request);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "offline-only");
            Authenticate?.Invoke(request, token);
            return Task.CompletedTask;
        }

        /// <summary>Records requested waits and advances virtual time unless a test overrides the wait behavior.</summary>
        private Task DelayAsync(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Delays.Add(duration);
            if (DelayOverride != null)
            {
                return DelayOverride(duration, token);
            }
            Clock.Advance(duration);
            return Task.CompletedTask;
        }

        /// <summary>Releases every fake response including responses queued beyond a deliberately bounded attempt limit.</summary>
        public void Dispose()
        {
            Http.Dispose();
            foreach (var response in Responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>Separates monotonic elapsed time from wall-clock response timestamps without using real timers.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private TimeSpan elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>Returns a fixed UTC epoch plus deterministic elapsed time for receipt and HTTP-date assertions.</summary>
        public override DateTimeOffset GetUtcNow() => Epoch + elapsed;

        /// <summary>Returns monotonic ticks used by the production deadline and minimum-delay calculations.</summary>
        public override long GetTimestamp() => elapsed.Ticks;

        /// <summary>Advances simulated HTTP, authentication, stream, or delay time without sleeping.</summary>
        internal void Advance(TimeSpan duration) => elapsed += duration;
    }

    /// <summary>Returns only prequeued responses and fails immediately if code attempts an unexpected additional HTTP call.</summary>
    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly ManualClock clock;
        internal Queue<TrackingResponse> Responses { get; } = new();
        internal List<HttpRequestMessage> Requests { get; } = new();
        internal List<TimeSpan> SendTimes { get; } = new();
        internal List<string> Accepts { get; } = new();
        internal List<string> Authorizations { get; } = new();
        internal Action<int> BeforeSend { get; set; }
        internal Exception SendError { get; set; }
        internal bool Disposed { get; private set; }

        /// <summary>Captures the same virtual clock used by the bounded reader and its injected retry delay.</summary>
        internal QueueHandler(ManualClock clock) => this.clock = clock;

        /// <summary>Records GET identity and headers, then returns one fake response with explicit request provenance.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            SendTimes.Add(clock.GetElapsedTime(0));
            Accepts.Add(string.Join(", ", request.Headers.Accept));
            Authorizations.Add(request.Headers.Authorization?.ToString());
            BeforeSend?.Invoke(Requests.Count);
            if (SendError != null)
            {
                return Task.FromException<HttpResponseMessage>(SendError);
            }
            if (Responses.Count == 0)
            {
                throw new InvalidOperationException("Unexpected additional offline JSON request.");
            }
            var response = Responses.Dequeue();
            response.RequestMessage ??= request;
            return Task.FromResult<HttpResponseMessage>(response);
        }

        /// <summary>Exposes handler lifetime so borrowed HTTP client ownership can be asserted without live traffic.</summary>
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Records response disposal before retry delays and terminal returns.</summary>
    private sealed class TrackingResponse : HttpResponseMessage
    {
        internal bool Disposed { get; private set; }

        /// <summary>Creates a fake HTTP response with the requested status code.</summary>
        internal TrackingResponse(HttpStatusCode status) : base(status) { }

        /// <summary>Records disposal while retaining the normal content and stream disposal contract.</summary>
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Tracks bounded body reads, optional unknown length, deterministic faults, and disposal.</summary>
    private sealed class TrackingStream : MemoryStream
    {
        internal bool ExposeLength { get; set; } = true;
        internal int ReadBytes { get; private set; }
        internal bool Disposed { get; private set; }
        internal Exception ReadError { get; set; }
        internal Action BeforeRead { get; set; }
        public override bool CanSeek => ExposeLength && base.CanSeek;

        /// <summary>Creates the exact in-memory byte sequence returned by a fake transport response.</summary>
        internal TrackingStream(byte[] bytes) : base(bytes) { }

        /// <summary>Tracks actual streamed bytes and injects elapsed-time or I/O failures before a bounded asynchronous read.</summary>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BeforeRead?.Invoke();
            if (ReadError != null)
            {
                throw ReadError;
            }
            int count = await base.ReadAsync(buffer, cancellationToken);
            ReadBytes += count;
            return count;
        }

        /// <summary>Records stream disposal for retry ordering, byte-limit, and cancellation assertions.</summary>
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
