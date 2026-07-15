using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ppgsm.Api;
using Ppgsm.Collectors.Authentication;
using Ppgsm.Core.Domain;

namespace Ppgsm.Api.Tests;

public sealed class ExternalConsentRevocationTests
{
    private static readonly Guid CustomerId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid ClientAppId = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid ServicePrincipalId = Guid.Parse("40000000-0000-0000-0000-000000000004");
    private static readonly Guid GrantId = Guid.Parse("50000000-0000-0000-0000-000000000005");

    [Fact]
    public void Disabled_configuration_does_not_require_external_adapter_values()
    {
        new ExternalConsentRevocationOptions().Validate();
    }

    [Theory]
    [InlineData(EnterpriseApplicationOffboardingPolicy.Preserve)]
    [InlineData(EnterpriseApplicationOffboardingPolicy.Disable)]
    [InlineData(EnterpriseApplicationOffboardingPolicy.Remove)]
    public void Enabled_configuration_accepts_only_explicit_enterprise_application_policy(
        EnterpriseApplicationOffboardingPolicy policy)
    {
        new ExternalConsentRevocationOptions
        {
            Enabled = true,
            ClientApplicationId = ClientAppId.ToString("D"),
            EnterpriseApplicationPolicy = policy
        }.Validate();
    }

    [Fact]
    public void Enabled_configuration_rejects_unallowlisted_power_platform_endpoint()
    {
        var options = new ExternalConsentRevocationOptions
        {
            Enabled = true,
            ClientApplicationId = ClientAppId.ToString("D"),
            PowerPlatformRbacEndpoint = "https://example.com/assignments/{assignmentId}"
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Enabled_configuration_rejects_non_microsoft_graph_base_url()
    {
        var options = new ExternalConsentRevocationOptions
        {
            Enabled = true,
            GraphBaseUrl = "https://example.com/",
            ClientApplicationId = ClientAppId.ToString("D")
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public async Task Already_revoked_is_idempotent_and_produces_immutable_evidence()
    {
        var transport = new FakeTransport(
            Response(HttpStatusCode.OK, "{\"value\":[]}", "resolve-request"),
            Response(HttpStatusCode.OK, "{\"value\":[]}", "grants-request"));

        var first = await Adapter(transport).RevokeAsync(CustomerId, CancellationToken.None);

        Assert.Equal(ExternalConsentRevocationStatus.AlreadyRevoked, first.Status);
        Assert.True(first.Succeeded);
        Assert.StartsWith("sha256:", first.EvidenceReference);
        Assert.Equal(2, first.Evidence.Count);
        Assert.All(transport.Calls, call => Assert.Equal(TenantId, call.TenantId));
        Assert.DoesNotContain(transport.Calls, call => call.Method != HttpMethod.Get);

        var repeated = await Adapter(new FakeTransport(
            Response(HttpStatusCode.OK, "{\"value\":[]}", "resolve-request"),
            Response(HttpStatusCode.OK, "{\"value\":[]}", "grants-request")))
            .RevokeAsync(CustomerId, CancellationToken.None);
        Assert.Equal(first.EvidenceReference, repeated.EvidenceReference);
    }

    [Fact]
    public async Task Wrong_service_principal_fails_before_any_write()
    {
        var wrongId = Guid.NewGuid();
        var transport = new FakeTransport(Response(HttpStatusCode.OK,
            $"{{\"value\":[{{\"id\":\"{wrongId:D}\",\"appId\":\"{ClientAppId:D}\",\"accountEnabled\":true}}]}}",
            "resolve-request"));

        var result = await Adapter(transport).RevokeAsync(CustomerId, CancellationToken.None);

        Assert.Equal(ExternalConsentRevocationStatus.Failed, result.Status);
        Assert.Single(transport.Calls);
        Assert.Equal(HttpMethod.Get, transport.Calls[0].Method);
    }

    [Fact]
    public async Task Failed_grant_delete_returns_partial_and_does_not_touch_enterprise_application()
    {
        var transport = new FakeTransport(
            ServicePrincipalResponse(),
            GrantsResponse(),
            Response(HttpStatusCode.Forbidden, "{\"error\":\"denied\"}", "delete-request"));

        var result = await Adapter(transport, EnterpriseApplicationOffboardingPolicy.Remove)
            .RevokeAsync(CustomerId, CancellationToken.None);

        Assert.Equal(ExternalConsentRevocationStatus.Partial, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(3, transport.Calls.Count);
        Assert.EndsWith($"oauth2PermissionGrants/{GrantId:D}", transport.Calls[^1].Endpoint.AbsoluteUri);
    }

    [Fact]
    public async Task Verified_only_power_platform_assignment_returns_pending_manual_action()
    {
        var transport = new FakeTransport(
            ServicePrincipalResponse(),
            GrantsResponse(),
            Response(HttpStatusCode.NoContent, string.Empty, "delete-request"));

        var result = await Adapter(transport, rbacAssignmentId: "verified")
            .RevokeAsync(CustomerId, CancellationToken.None);

        Assert.Equal(ExternalConsentRevocationStatus.PendingManualAction, result.Status);
        Assert.False(result.Succeeded);
        Assert.Contains("concrete assignment ID", result.Detail);
        Assert.Equal(3, transport.Calls.Count);
    }

    [Fact]
    public async Task Http_transport_retries_throttling_and_preserves_final_request_evidence()
    {
        var handler = new QueueHttpHandler(
            HttpResponse(HttpStatusCode.TooManyRequests, "throttled", retryAfter: TimeSpan.FromMilliseconds(1)),
            HttpResponse(HttpStatusCode.NoContent, string.Empty, requestId: "final-request"));
        var transport = new MicrosoftExternalRevocationTransport(
            new HttpClient(handler), new FakeTokenAcquirer(), TimeProvider.System);

        var result = await transport.SendAsync(TenantId, "https://graph.microsoft.com/.default", HttpMethod.Delete,
            new Uri($"https://graph.microsoft.com/v1.0/oauth2PermissionGrants/{GrantId:D}"), null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);
        Assert.Equal("final-request", result.RequestId);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(TenantId, Assert.Single(FakeTokenAcquirer.Tenants));
    }

    private static ExternalConsentRevocationAdapter Adapter(
        FakeTransport transport,
        EnterpriseApplicationOffboardingPolicy policy = EnterpriseApplicationOffboardingPolicy.Preserve,
        string? rbacAssignmentId = null) =>
        new(new ExternalConsentRevocationOptions
        {
            Enabled = true,
            ClientApplicationId = ClientAppId.ToString("D"),
            EnterpriseApplicationPolicy = policy
        }, new FakeCustomerStore(), new FakeConnectionStore(rbacAssignmentId), transport);

    private static ExternalRevocationResponse ServicePrincipalResponse() => Response(HttpStatusCode.OK,
        $"{{\"value\":[{{\"id\":\"{ServicePrincipalId:D}\",\"appId\":\"{ClientAppId:D}\",\"accountEnabled\":true}}]}}",
        "resolve-request");

    private static ExternalRevocationResponse GrantsResponse() => Response(HttpStatusCode.OK,
        $"{{\"value\":[{{\"id\":\"{GrantId:D}\",\"clientId\":\"{ServicePrincipalId:D}\"}}]}}",
        "grants-request");

    private static ExternalRevocationResponse Response(HttpStatusCode status, string body, string requestId)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return new(status, bytes, requestId, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static HttpResponseMessage HttpResponse(HttpStatusCode status, string body, string? requestId = null, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        if (requestId is not null) response.Headers.TryAddWithoutValidation("request-id", requestId);
        if (retryAfter is not null) response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
        return response;
    }

    private sealed class FakeCustomerStore : ICustomerStore
    {
        public ValueTask<Customer?> FindCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Customer?>(customerId == CustomerId
                ? new(CustomerId, "Customer", TenantId, "westeurope", CustomerStatus.Active, DateTimeOffset.UtcNow)
                : null);

        public ValueTask<Customer> CreateCustomerAsync(string name, Guid entraTenantId, string region, SubjectIdentity creator, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConnectionStore(string? rbacAssignmentId) : ITenantConnectionStore
    {
        public ValueTask<TenantConnection?> FindAsync(Guid customerId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<TenantConnection?>(customerId == CustomerId
                ? new(Guid.NewGuid(), CustomerId, ConnectionMode.Delegated, ClientAppId, ServicePrincipalId,
                    rbacAssignmentId, false, null, null, null, ConnectionStatus.Active, DateTimeOffset.UtcNow)
                : null);

        public ValueTask<TenantConnection> SaveAsync(TenantConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<TenantCapability>> ListCapabilitiesAsync(Guid customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask ReplaceCapabilitiesAsync(Guid customerId, Guid connectionId, IReadOnlyCollection<TenantCapability> capabilities, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeTransport(params ExternalRevocationResponse[] responses) : IExternalRevocationTransport
    {
        private readonly Queue<ExternalRevocationResponse> _responses = new(responses);
        public List<Call> Calls { get; } = [];

        public ValueTask<ExternalRevocationResponse> SendAsync(Guid tenantId, string resourceScope, HttpMethod method,
            Uri endpoint, string? jsonBody, CancellationToken cancellationToken)
        {
            Calls.Add(new(tenantId, resourceScope, method, endpoint, jsonBody));
            return ValueTask.FromResult(_responses.Dequeue());
        }
    }

    private sealed class QueueHttpHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class FakeTokenAcquirer : IAppOnlyTokenAcquirer
    {
        public static List<Guid> Tenants { get; } = [];

        public ValueTask<string> GetAccessTokenAsync(Guid entraTenantId, string resourceScope, CancellationToken cancellationToken)
        {
            Tenants.Clear();
            Tenants.Add(entraTenantId);
            return ValueTask.FromResult("test-token");
        }
    }

    private sealed record Call(Guid TenantId, string ResourceScope, HttpMethod Method, Uri Endpoint, string? JsonBody);
}