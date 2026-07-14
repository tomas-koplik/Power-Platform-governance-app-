using Ppgsm.Api;
using Ppgsm.Collectors.Authentication;

namespace Ppgsm.Api.Tests;

public sealed class TenantConsentVerifierTests
{
    private static readonly Guid TenantId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid AdminId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid ClientAppId = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid ResourceAppId = Guid.Parse("40000000-0000-0000-0000-000000000004");

    [Fact]
    public async Task Verifies_strict_tenant_service_principal_scope_admin_and_all_capabilities()
    {
        var evidence = await Verifier(Facts(), VerifiedProbe()).VerifyAsync(TenantId, new(TenantId, AdminId), CancellationToken.None);

        Assert.True(evidence.Activatable);
        Assert.Equal(AdminId.ToString("D"), evidence.VerifiedAdminObjectId);
        Assert.All(evidence.Capabilities, value => Assert.NotNull(value.RawResponseSha256));
    }

    [Fact]
    public async Task Rejects_wrong_authenticated_tenant_before_transports_run()
    {
        var graph = new FakeGraph(Facts());
        await Assert.ThrowsAsync<OnboardingValidationException>(async () =>
            await Verifier(graph, VerifiedProbe()).VerifyAsync(TenantId, new(Guid.NewGuid(), AdminId), CancellationToken.None));
        Assert.Equal(0, graph.CallCount);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Wrong_service_principal_missing_scope_or_non_admin_degrades(
        bool servicePrincipalEnabled,
        bool scopeGranted,
        bool adminAssigned)
    {
        var scopes = scopeGranted ? new[] { "Environment.Read.All" } : Array.Empty<string>();
        var facts = Facts(servicePrincipalEnabled, scopes, adminAssigned);

        var evidence = await Verifier(facts, VerifiedProbe()).VerifyAsync(TenantId, new(TenantId, AdminId), CancellationToken.None);

        Assert.False(evidence.Activatable);
    }

    [Fact]
    public async Task Partial_or_preview_only_capability_results_degrade()
    {
        var partial = await Verifier(Facts(), new FakeProbes(CapabilityVerificationState.Unavailable))
            .VerifyAsync(TenantId, new(TenantId, AdminId), CancellationToken.None);
        var preview = await Verifier(Facts(), new FakeProbes(CapabilityVerificationState.PreviewOnly))
            .VerifyAsync(TenantId, new(TenantId, AdminId), CancellationToken.None);

        Assert.False(partial.Activatable);
        Assert.False(preview.Activatable);
    }

    [Fact]
    public async Task Revoked_enterprise_application_degrades_on_reverification()
    {
        var evidence = await Verifier(Facts(servicePrincipalEnabled: false), VerifiedProbe())
            .VerifyAsync(TenantId, new(TenantId, AdminId), CancellationToken.None);

        Assert.False(evidence.Activatable);
        Assert.False(evidence.EnterpriseApplicationPresent);
    }

    private static LiveTenantConsentVerifier Verifier(GraphConsentFacts facts, IConsentCapabilityProbeTransport probes) =>
        Verifier(new FakeGraph(facts), probes);

    private static LiveTenantConsentVerifier Verifier(IConsentGraphTransport graph, IConsentCapabilityProbeTransport probes) =>
        new(Options(), graph, probes);

    private static TenantConsentVerificationOptions Options() => new()
    {
        ClientApplicationId = ClientAppId.ToString("D"),
        DelegatedResourceApplicationId = ResourceAppId.ToString("D"),
        ExpectedDelegatedScopes = ["Environment.Read.All"],
        CapabilityProbes = [new()
        {
            Name = "environments",
            Endpoint = "https://api.powerplatform.com/powerplatform/environments?api-version=2022-03-01-preview",
            ResourceScope = CollectorResources.PowerPlatform
        }]
    };

    private static GraphConsentFacts Facts(
        bool servicePrincipalEnabled = true,
        IReadOnlyCollection<string>? scopes = null,
        bool adminAssigned = true) =>
        new(TenantId, servicePrincipalEnabled ? Guid.NewGuid().ToString("D") : null, servicePrincipalEnabled,
            scopes ?? ["Environment.Read.All"], adminAssigned, "fixture");

    private static FakeProbes VerifiedProbe() => new(CapabilityVerificationState.Verified);

    private sealed class FakeGraph(GraphConsentFacts facts) : IConsentGraphTransport
    {
        public int CallCount { get; private set; }
        public ValueTask<GraphConsentFacts> ReadAsync(Guid tenantId, Guid clientApplicationId, Guid resourceApplicationId, Guid adminObjectId, CancellationToken cancellationToken)
        {
            CallCount++;
            Assert.Equal(ClientAppId, clientApplicationId);
            Assert.Equal(ResourceAppId, resourceApplicationId);
            Assert.Equal(AdminId, adminObjectId);
            return ValueTask.FromResult(facts);
        }
    }

    private sealed class FakeProbes(CapabilityVerificationState state) : IConsentCapabilityProbeTransport
    {
        public ValueTask<EndpointCapabilityVerification> ProbeAsync(Guid tenantId, AuthenticatedCallbackIdentity adminIdentity, ConsentCapabilityProbe probe, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EndpointCapabilityVerification(probe.Endpoint, "app-only", state, 200, "request-id", "raw-sha256", state.ToString()));
    }
}