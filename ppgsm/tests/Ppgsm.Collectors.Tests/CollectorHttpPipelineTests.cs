using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Ppgsm.Collectors;
using Ppgsm.Collectors.Authentication;
using Ppgsm.Collectors.Transport;
using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Collectors.Tests;

public sealed class CollectorHttpPipelineTests
{
    [Fact]
    public async Task Preserves_unknown_fixture_fields_byte_for_byte_before_counting()
    {
        var fixture = await FixtureAsync("tenant-settings-unknown.json");
        var runtime = new LocalCollectorRuntimeStore();
        var pipeline = CreatePipeline(new SequenceHandler(Json(HttpStatusCode.OK, fixture)), runtime);

        var result = await pipeline.CollectPagesAsync(Context(), "tenantSettings", Request("settings"), runtime, CancellationToken.None);

        Assert.Equal(SectionCoverage.Full, result.Coverage);
        Assert.Equal(fixture, Assert.Single(runtime.Evidence).Content);
        Assert.Contains("futurePreviewProperty", System.Text.Encoding.UTF8.GetString(Assert.Single(runtime.Evidence).Content));
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal("tenantSettings", evidence.CollectorId);
        Assert.Equal("GET", evidence.Method);
        Assert.Equal(200, evidence.StatusCode);
        Assert.Equal(1, evidence.PageNumber);
        Assert.Equal(1, evidence.AttemptNumber);
        Assert.Equal(SnapshotMode.Delegated, evidence.AuthMode);
        Assert.Equal(CollectorResources.BusinessApplications, evidence.TokenResource);
        Assert.DoesNotContain("fixture-token", evidence.RedactedHeaders.Values);
    }

    [Fact]
    public async Task Follows_next_link_and_counts_all_fixture_items()
    {
        var page1 = await FixtureAsync("environments-page-1.json");
        var page2 = await FixtureAsync("environments-page-2.json");
        var handler = new SequenceHandler(Json(HttpStatusCode.OK, page1), Json(HttpStatusCode.OK, page2));
        var runtime = new LocalCollectorRuntimeStore();
        var pipeline = CreatePipeline(handler, runtime);

        var result = await pipeline.CollectPagesAsync(Context(), "environments", Request("environments"), runtime, CancellationToken.None);

        Assert.Equal(SectionCoverage.Full, result.Coverage);
        Assert.Equal(3, result.ItemCount);
        Assert.Equal(2, result.Evidence.Count);
        Assert.Equal("https://fixture.test/environments?page=2", handler.Requests[1].AbsoluteUri);
    }

    [Theory]
    [InlineData("http://fixture.test/environments")]
    [InlineData("https://user@fixture.test/environments")]
    [InlineData("https://evil.test/environments")]
    public async Task Rejects_malicious_configured_route_before_token_or_send(string route)
    {
        var runtime = new LocalCollectorRuntimeStore();
        var handler = new SequenceHandler(Json(HttpStatusCode.OK, []));
        var tokens = new CountingTokenProvider();
        var pipeline = CreatePipeline(handler, runtime, tokens);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.CollectPagesAsync(
            Context(), "route", Request(new Uri(route)), runtime, CancellationToken.None));

        Assert.Equal(0, tokens.Calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rejects_cross_origin_next_link_without_sending_token_to_second_host()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("{\"value\":[{}],\"nextLink\":\"https://evil.test/steal\"}");
        var runtime = new LocalCollectorRuntimeStore();
        var handler = new SequenceHandler(Json(HttpStatusCode.OK, body));
        var tokens = new CountingTokenProvider();
        var pipeline = CreatePipeline(handler, runtime, tokens);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.CollectPagesAsync(Context(), "next", Request("next"), runtime, CancellationToken.None));

        Assert.Equal(1, tokens.Calls);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Rejects_malformed_next_link_without_another_token_or_send()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("{\"value\":[{}],\"nextLink\":\"/relative\"}");
        var runtime = new LocalCollectorRuntimeStore();
        var handler = new SequenceHandler(Json(HttpStatusCode.OK, body));
        var tokens = new CountingTokenProvider();
        var pipeline = CreatePipeline(handler, runtime, tokens);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.CollectPagesAsync(Context(), "next", Request("next"), runtime, CancellationToken.None));

        Assert.Equal(1, tokens.Calls);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Rejects_allowlisted_private_destination_before_token_or_send()
    {
        var runtime = new LocalCollectorRuntimeStore();
        var handler = new SequenceHandler(Json(HttpStatusCode.OK, []));
        var tokens = new CountingTokenProvider();
        var pipeline = CreatePipeline(handler, runtime, tokens, allowedPrefix: "https://127.0.0.1/");

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.CollectPagesAsync(
            Context(), "private", Request(new Uri("https://127.0.0.1/private")), runtime, CancellationToken.None));

        Assert.Equal(0, tokens.Calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Does_not_follow_redirect_or_send_token_to_redirect_destination()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://evil.test/steal");
        redirect.Content = new ByteArrayContent([]);
        var runtime = new LocalCollectorRuntimeStore();
        var handler = new SequenceHandler(redirect);
        var tokens = new CountingTokenProvider();
        var pipeline = CreatePipeline(handler, runtime, tokens);

        var result = await pipeline.CollectPagesAsync(Context(), "redirect", Request("redirect"), runtime, CancellationToken.None);

        Assert.Equal(SectionCoverage.Failed, result.Coverage);
        Assert.Equal(1, tokens.Calls);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Retains_401_and_403_bodies_as_failed_evidence(HttpStatusCode statusCode)
    {
        var body = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"permission evidence\"}");
        var runtime = new LocalCollectorRuntimeStore();
        var pipeline = CreatePipeline(new SequenceHandler(Json(statusCode, body)), runtime);

        var result = await pipeline.CollectPagesAsync(Context(), "restricted", Request("restricted"), runtime, CancellationToken.None);

        Assert.Equal(SectionCoverage.Failed, result.Coverage);
        Assert.Equal(body, Assert.Single(runtime.Evidence).Content);
        Assert.Contains(((int)statusCode).ToString(), Assert.Single(result.Warnings));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Retries_429_and_5xx_then_captures_success(HttpStatusCode transientStatus)
    {
        var retry = Json(transientStatus, System.Text.Encoding.UTF8.GetBytes("{\"retry\":true}"));
        retry.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        var handler = new SequenceHandler(retry, Json(HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes("{\"value\":[{}]}")));
        var runtime = new LocalCollectorRuntimeStore();
        var pipeline = CreatePipeline(handler, runtime);

        var result = await pipeline.CollectPagesAsync(Context(), "retry", Request("retry"), runtime, CancellationToken.None);

        Assert.Equal(SectionCoverage.Full, result.Coverage);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, runtime.Evidence.Count);
    }

    [Fact]
    public async Task Marks_partial_when_a_later_page_fails_and_retains_both_pages()
    {
        var page1 = await FixtureAsync("environments-page-1.json");
        var handler = new SequenceHandler(
            Json(HttpStatusCode.OK, page1),
            Json(HttpStatusCode.BadGateway, System.Text.Encoding.UTF8.GetBytes("{\"error\":\"upstream\"}")));
        var runtime = new LocalCollectorRuntimeStore();
        var pipeline = CreatePipeline(handler, runtime, maxRetries: 0);

        var result = await pipeline.CollectPagesAsync(Context(), "partial", Request("partial"), runtime, CancellationToken.None);

        Assert.Equal(SectionCoverage.Partial, result.Coverage);
        Assert.Equal(2, result.ItemCount);
        Assert.Equal(2, runtime.Evidence.Count);
    }

    [Fact]
    public async Task Propagates_cancellation_without_fabricating_section_coverage()
    {
        var runtime = new LocalCollectorRuntimeStore();
        var handler = new CallbackHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        });
        var pipeline = CreatePipeline(handler, runtime);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.CollectPagesAsync(Context(), "cancel", Request("cancel"), runtime, cancellation.Token));
        Assert.Empty(runtime.Evidence);
    }

    [Fact]
    public async Task Resumes_at_persisted_next_link_after_partial_failure()
    {
        var page1 = await FixtureAsync("environments-page-1.json");
        var runtime = new LocalCollectorRuntimeStore();
        var first = CreatePipeline(new SequenceHandler(
            Json(HttpStatusCode.OK, page1),
            Json(HttpStatusCode.ServiceUnavailable, System.Text.Encoding.UTF8.GetBytes("{}"))), runtime, maxRetries: 0);
        var partial = await first.CollectPagesAsync(Context(), "resume", Request("resume"), runtime, CancellationToken.None);
        Assert.Equal(SectionCoverage.Partial, partial.Coverage);
        var checkpoint = await runtime.ReadAsync(Context().CustomerId, Context().SnapshotId, "resume", CancellationToken.None);
        Assert.NotNull(checkpoint);
        Assert.Equal(checkpoint.Evidence.Select(item => item.RawEvidenceReferenceId), checkpoint.EvidenceIds);

        var page2 = await FixtureAsync("environments-page-2.json");
        var resumedHandler = new SequenceHandler(Json(HttpStatusCode.OK, page2));
        var resumed = CreatePipeline(resumedHandler, runtime);
        var result = await resumed.CollectPagesAsync(Context(), "resume", Request("ignored-on-resume"), runtime, CancellationToken.None);

        Assert.Equal(SectionCoverage.Full, result.Coverage);
        Assert.Equal("https://fixture.test/environments?page=2", Assert.Single(resumedHandler.Requests).AbsoluteUri);
        Assert.Equal(2, result.Evidence.Count);
        Assert.Equal(result.Evidence.First().RawEvidenceReferenceId, result.Evidence.Last().PreviousEvidenceId);
    }

    private static CollectorHttpPipeline CreatePipeline(
        HttpMessageHandler handler,
        LocalCollectorRuntimeStore runtime,
        ICollectorTokenProvider? tokens = null,
        int maxRetries = 2,
        string allowedPrefix = "https://fixture.test/")
    {
        var options = new CollectorTransportOptions
        {
            MaxPerTenantConcurrency = 4,
            MaxRetryAttempts = maxRetries,
            MaxRetryDelaySeconds = 1,
            AllowedRoutePrefixes = new() { [CollectorResources.BusinessApplications] = [allowedPrefix] }
        };
        return new(
            new HttpClient(handler),
            tokens ?? new StaticTokenProvider(),
            runtime,
            runtime,
            new TenantConcurrencyLimiter(options),
            new CollectorDestinationPolicy(options, new PublicTestResolver()),
            options,
            TimeProvider.System);
    }

    private static SnapshotCollectorContext Context() => new(
        new TenantContext(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            SubjectIdentity.Create(Guid.Parse("20000000-0000-0000-0000-000000000001"), Guid.Parse("30000000-0000-0000-0000-000000000001")),
            MembershipRole.CustomerAdmin),
        Guid.Parse("20000000-0000-0000-0000-000000000002"),
        Guid.Parse("30000000-0000-0000-0000-000000000003"),
        SnapshotMode.Delegated,
        "fixture-user",
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "fixture-user")], "fixture")),
        CollectorConfidence.Preview);

    private static CollectorApiRequest Request(string path) => new(
        HttpMethod.Get,
        new Uri($"https://fixture.test/{path}"),
        CollectorResources.BusinessApplications,
        "fixture-v1");

    private static CollectorApiRequest Request(Uri uri) => new(
        HttpMethod.Get, uri, CollectorResources.BusinessApplications, "fixture-v1");

    private static HttpResponseMessage Json(HttpStatusCode statusCode, byte[] body) => new(statusCode)
    {
        Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }
    };

    private static Task<byte[]> FixtureAsync(string name) => File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private sealed class StaticTokenProvider : ICollectorTokenProvider
    {
        public ValueTask<string> GetAccessTokenAsync(CollectorTokenRequest request, CancellationToken cancellationToken) => ValueTask.FromResult("fixture-token");
    }

    private sealed class CountingTokenProvider : ICollectorTokenProvider
    {
        public int Calls { get; private set; }
        public ValueTask<string> GetAccessTokenAsync(CollectorTokenRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult("fixture-token");
        }
    }

    private sealed class PublicTestResolver : ICollectorDestinationResolver
    {
        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new[] { IPAddress.Parse("203.0.113.10") });
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}