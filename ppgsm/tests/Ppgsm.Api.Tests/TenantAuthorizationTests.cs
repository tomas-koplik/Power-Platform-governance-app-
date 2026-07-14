using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ppgsm.Api.Tests;

public sealed class TenantAuthorizationTests : IClassFixture<DevelopmentApiFactory>
{
    private readonly DevelopmentApiFactory _factory;

    public TenantAuthorizationTests(DevelopmentApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Workspace_is_derived_from_authenticated_subject_membership()
    {
        var owner = Client(Guid.NewGuid(), Guid.NewGuid());
        var customerId = await CreateCustomerAsync(owner);
        var outsider = Client(Guid.NewGuid(), Guid.NewGuid());

        using var response = await outsider.GetAsync("/api/v1/session/workspace");
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.DoesNotContain(payload.RootElement.GetProperty("customers").EnumerateArray(),
            customer => customer.GetProperty("customerId").GetGuid() == customerId);
    }

    [Fact]
    public async Task App_only_snapshot_is_rejected_without_durable_enqueue()
    {
        var owner = Client(Guid.NewGuid(), Guid.NewGuid());
        var customerId = await CreateCustomerAsync(owner);
        using var request = Json(HttpMethod.Post, $"/api/v1/customers/{customerId}/snapshots", new { mode = "AppOnly" });
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        using var response = await owner.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("durable snapshot enqueue", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Every_new_customer_endpoint_denies_cross_tenant_subject()
    {
        var customerId = await CreateCustomerAsync(Client(Guid.NewGuid(), Guid.NewGuid()));
        var outsider = Client(Guid.NewGuid(), Guid.NewGuid());
        var snapshotId = Guid.NewGuid();
        var findingId = Guid.NewGuid();
        var exportId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var requests = new HttpRequestMessage[]
        {
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/snapshots/{snapshotId}/findings"),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/snapshots/{snapshotId}/evidence?page=1&pageSize=50"),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/snapshots/{snapshotId}/evidence/tenant-settings"),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/snapshots/{snapshotId}/evidence/environments"),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/snapshots/{snapshotId}/evidence/dlp-policies"),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/consent-metadata"),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/snapshots/{snapshotId}/evidence"),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/consent-metadata"),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/snapshots/{snapshotId}/score"),
            Json(HttpMethod.Post, $"/api/v1/customers/{customerId}/comparisons", new { baselineSnapshotId = snapshotId, currentSnapshotId = Guid.NewGuid() }),
            Json(HttpMethod.Post, $"/api/v1/customers/{customerId}/exports", new { format = "Json", snapshotId }),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/exports/{exportId}"),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/exports/{exportId}/download"),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/exports/{exportId}/download"),
            new(HttpMethod.Post, $"/api/v1/customers/{customerId}/exports/{exportId}/download-url"),
            Json(HttpMethod.Post, $"/api/v1/customers/{customerId}/offboarding", new { retentionExpiresAt = now.AddDays(30) }),
            new(HttpMethod.Post, $"/api/v1/customers/{customerId}/offboarding/approve"),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/deletion/certificate"),
            Json(HttpMethod.Post, $"/api/v1/customers/{customerId}/findings/{findingId}/exceptions", new { reason = "Accepted risk", expiresAt = now.AddDays(1) }),
            Json(HttpMethod.Post, $"/api/v1/customers/{customerId}/remediation/proposals", new { findingId, snapshotId, templateId = "trusted.v1", parameters = new { }, evidenceHash = "hash", targetScope = customerId.ToString("D"), evidenceCapturedAt = now, evidenceValidUntil = now.AddHours(1) }),
            new(HttpMethod.Get, $"/api/v1/customers/{customerId}/snapshots/{snapshotId}/findings/{findingId}/remediation-eligibility?evidenceHash=hash&evidenceCapturedAt={Uri.EscapeDataString(now.ToString("O"))}&evidenceValidUntil={Uri.EscapeDataString(now.AddHours(1).ToString("O"))}"),
            Json(HttpMethod.Post, $"/api/v1/customers/{customerId}/remediation/proposals/{proposalId}/review", new { approved = true, latestSnapshotId = snapshotId })
        };

        foreach (var request in requests)
        {
            using (request)
            using (var response = await outsider.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task Same_oid_from_different_tenant_cannot_use_membership()
    {
        var objectId = Guid.NewGuid();
        var customerId = await CreateCustomerAsync(Client(Guid.NewGuid(), objectId));

        using var response = await Client(Guid.NewGuid(), objectId).GetAsync($"/api/v1/customers/{customerId}/snapshots");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null, "22222222-2222-2222-2222-222222222222")]
    [InlineData("11111111-1111-1111-1111-111111111111", null)]
    [InlineData("not-a-guid", "22222222-2222-2222-2222-222222222222")]
    public void Composite_subject_requires_valid_tid_and_oid(string? tenant, string? subject)
    {
        var claims = new List<System.Security.Claims.Claim>();
        if (tenant is not null) claims.Add(new("tid", tenant));
        if (subject is not null) claims.Add(new("oid", subject));
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test"));

        Assert.Throws<Ppgsm.Core.Domain.TenantAccessDeniedException>(() => principal.Subject());
    }

    [Fact]
    public void Remediation_request_contract_rejects_client_script_text_by_construction()
    {
        Assert.DoesNotContain(typeof(Ppgsm.Api.CreateRemediationProposalRequest).GetProperties(),
            property => property.Name.Contains("Script", StringComparison.OrdinalIgnoreCase));
    }

    private HttpClient Client(Guid tenantId, Guid objectId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Development-Tenant", tenantId.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Development-Subject", objectId.ToString("D"));
        return client;
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/customers", new { name = $"Customer-{Guid.NewGuid():N}", entraTenantId = Guid.NewGuid(), region = "westeurope" });
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("customerId").GetGuid();
    }

    private static HttpRequestMessage Json(HttpMethod method, string uri, object body) => new(method, uri) { Content = JsonContent.Create(body) };
}

public sealed class DevelopmentApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
}