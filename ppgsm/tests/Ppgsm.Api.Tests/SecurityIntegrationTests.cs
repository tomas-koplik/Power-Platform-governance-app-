using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Api.Tests;

public sealed class SecurityIntegrationTests
{
    [Fact]
    public void Api_access_rejects_wrong_scope_or_calling_client()
    {
        var options = new ApiAccessOptions { Scope = "ppgsm.read", Audiences = ["ppgsm-api"], AuthorizedClientIds = ["trusted-client"] };

        Assert.False(ApiAccessPolicy.IsAuthorized(Principal("other.scope", "trusted-client"), options));
        Assert.False(ApiAccessPolicy.IsAuthorized(Principal("ppgsm.read", "untrusted-client"), options));
        Assert.False(ApiAccessPolicy.IsAuthorized(Principal("ppgsm.read", "trusted-client", "wrong-api"), options));
        Assert.True(ApiAccessPolicy.IsAuthorized(Principal("openid ppgsm.read", "trusted-client"), options));
    }

    [Fact]
    public async Task Audit_service_records_success_and_denied_outcomes()
    {
        var sink = new AuditSink();
        var service = new AuditService(sink, TimeProvider.System);
        var tenant = new TenantContext(Guid.NewGuid(), SubjectIdentity.Create(Guid.NewGuid(), Guid.NewGuid()), MembershipRole.Reader);
        var success = new DefaultHttpContext();
        success.Response.StatusCode = StatusCodes.Status200OK;
        var denied = new DefaultHttpContext();
        denied.Response.StatusCode = StatusCodes.Status403Forbidden;

        await service.RecordAsync(tenant, success, CancellationToken.None);
        await service.RecordAsync(tenant, denied, CancellationToken.None);

        Assert.Equal(["Success", "Denied"], sink.Events.Select(value => value.Outcome));
    }

    [Fact]
    public async Task Evidence_projection_redacts_pii_unless_raw_is_explicitly_authorized()
    {
        var reference = EvidenceReference();
        var json = "{\"displayName\":\"Ada Lovelace\",\"mail\":\"ada@example.test\",\"enabled\":true}";

        var projected = await RawEvidenceProjection.CreateAsync(new(reference, Stream(json)), false, CancellationToken.None);
        var redacted = JsonSerializer.Serialize(projected);
        var raw = await RawEvidenceProjection.CreateAsync(new(reference, Stream(json)), true, CancellationToken.None);
        var unredacted = JsonSerializer.Serialize(raw);

        Assert.DoesNotContain("Ada Lovelace", redacted);
        Assert.DoesNotContain("ada@example.test", redacted);
        Assert.Contains("[REDACTED]", redacted);
        Assert.Contains("Ada Lovelace", unredacted);
    }

    [Fact]
    public async Task Missing_live_verifier_keeps_connection_degraded()
    {
        var customerId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var store = new ConnectionStore();
        var service = new TenantConnectionService(store, new MembershipStore(), new UnavailableTenantConsentVerifier(), TimeProvider.System);
        var state = new OnboardingState(customerId, Guid.NewGuid(), Guid.NewGuid(), tenantId, "delegated-admin-consent", "nonce", DateTimeOffset.UtcNow.AddMinutes(5));

        var result = await service.VerifyConsentAsync(new(state, tenantId, new(tenantId, state.InitiatingObjectId)), CancellationToken.None);

        Assert.Equal(ConnectionStatus.Degraded, result.Connection.Status);
        Assert.False(result.Evidence.Activatable);
    }

    [Fact]
    public async Task Verification_evidence_cannot_substitute_tenant()
    {
        var expectedTenant = Guid.NewGuid();
        var state = new OnboardingState(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), expectedTenant, "delegated-admin-consent", "nonce", DateTimeOffset.UtcNow.AddMinutes(5));
        var service = new TenantConnectionService(new ConnectionStore(), new MembershipStore(), new SubstitutingVerifier(), TimeProvider.System);

        await Assert.ThrowsAsync<OnboardingValidationException>(async () =>
            await service.VerifyConsentAsync(new(state, expectedTenant, new(expectedTenant, state.InitiatingObjectId)), CancellationToken.None));
    }

    [Fact]
    public async Task Matching_verified_admin_activates_and_grants_server_membership()
    {
        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var state = new OnboardingState(Guid.NewGuid(), tenantId, adminId, tenantId, "delegated-admin-consent", "nonce", DateTimeOffset.UtcNow.AddMinutes(5));
        var connections = new ConnectionStore();
        var memberships = new MembershipStore();
        var service = new TenantConnectionService(connections, memberships, new VerifiedVerifier(adminId), TimeProvider.System);

        var result = await service.VerifyConsentAsync(new(state, tenantId, new(tenantId, adminId)), CancellationToken.None);

        Assert.Equal(ConnectionStatus.Active, result.Connection.Status);
        Assert.Equal(MembershipRole.CustomerAdmin, memberships.Granted?.Role);
        Assert.Single(connections.Capabilities);
    }

    private static ClaimsPrincipal Principal(string scope, string clientId, string audience = "ppgsm-api") => new(new ClaimsIdentity([
        new Claim("tid", Guid.NewGuid().ToString("D")),
        new Claim("oid", Guid.NewGuid().ToString("D")),
        new Claim("scp", scope),
        new Claim("aud", audience),
        new Claim("azp", clientId)], "test"));

    private static MemoryStream Stream(string value) => new(Encoding.UTF8.GetBytes(value));

    private static RawEvidenceReference EvidenceReference() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "users", "raw/test.json", "hash", "application/json", "v1",
        DateTimeOffset.UtcNow, CollectorConfidence.Validated, "collector", "1", "1", null, "GET", "/users", 200,
        "resource", SnapshotMode.Delegated, Guid.NewGuid(), "delegated-user", null, new Dictionary<string, string>(), 1, 1, null, "complete");

    private sealed class SubstitutingVerifier : ITenantConsentVerifier
    {
        public ValueTask<ConsentVerificationEvidence> VerifyAsync(Guid entraTenantId, AuthenticatedCallbackIdentity adminIdentity, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ConsentVerificationEvidence(Guid.NewGuid(), true, true, true, Guid.NewGuid().ToString(),
                adminIdentity.ObjectId.ToString(), [VerifiedCapability()], "verified"));
    }

    private sealed class VerifiedVerifier(Guid adminId) : ITenantConsentVerifier
    {
        public ValueTask<ConsentVerificationEvidence> VerifyAsync(Guid entraTenantId, AuthenticatedCallbackIdentity adminIdentity, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ConsentVerificationEvidence(entraTenantId, true, true, true, Guid.NewGuid().ToString(),
                adminId.ToString("D"), [VerifiedCapability()], "verified"));
    }

    private static EndpointCapabilityVerification VerifiedCapability() => new(
        "https://api.powerplatform.com/powerplatform/environments?api-version=2022-03-01-preview",
        "app-only", CapabilityVerificationState.Verified, 200, "request-id", "hash", "verified");

    private sealed class ConnectionStore : ITenantConnectionStore
    {
        private TenantConnection? _connection;
        public IReadOnlyCollection<TenantCapability> Capabilities { get; private set; } = [];
        public ValueTask<TenantConnection?> FindAsync(Guid customerId, CancellationToken cancellationToken) => ValueTask.FromResult(_connection);
        public ValueTask<TenantConnection> SaveAsync(TenantConnection connection, CancellationToken cancellationToken) { _connection = connection; return ValueTask.FromResult(connection); }
        public ValueTask<IReadOnlyList<TenantCapability>> ListCapabilitiesAsync(Guid customerId, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<TenantCapability>>([]);
        public ValueTask ReplaceCapabilitiesAsync(Guid customerId, Guid connectionId, IReadOnlyCollection<TenantCapability> capabilities, CancellationToken cancellationToken) { Capabilities = capabilities; return ValueTask.CompletedTask; }
    }

    private sealed class MembershipStore : ITenantMembershipStore
    {
        public TenantMembership? Granted { get; private set; }
        public ValueTask<TenantMembership?> FindAsync(SubjectIdentity subject, Guid customerId, CancellationToken cancellationToken) => ValueTask.FromResult<TenantMembership?>(null);
        public ValueTask<IReadOnlyList<TenantMembership>> ListForSubjectAsync(SubjectIdentity subject, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<TenantMembership>>([]);
        public ValueTask<TenantMembership> GrantAsync(Guid customerId, SubjectIdentity subject, MembershipRole role, CancellationToken cancellationToken)
        {
            Granted = new(Guid.NewGuid(), customerId, subject.TenantId, subject.ObjectId, role, DateTimeOffset.UtcNow);
            return ValueTask.FromResult(Granted);
        }
    }

    private sealed class AuditSink : IAuditSink
    {
        public List<AuditEvent> Events { get; } = [];
        public ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) { Events.Add(auditEvent); return ValueTask.CompletedTask; }
    }
}
