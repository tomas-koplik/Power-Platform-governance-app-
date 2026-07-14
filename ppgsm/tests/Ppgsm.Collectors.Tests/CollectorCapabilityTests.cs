using System.Security.Claims;
using Ppgsm.Collectors.Authentication;
using Ppgsm.Collectors.Clients;
using Ppgsm.Collectors.Collectors;
using Ppgsm.Collectors.Transport;
using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Collectors.Tests;

public sealed class CollectorCapabilityTests
{
    [Theory]
    [InlineData("tenantSettings")]
    [InlineData("environments")]
    public async Task Production_collectors_reject_app_only_until_T04_is_proven(string section)
    {
        var client = new FakeBapClient();
        ISnapshotCollector collector = section == "tenantSettings"
            ? new TenantSettingsCollector(client)
            : new EnvironmentsCollector(client);

        var result = await collector.CollectAsync(Context(SnapshotMode.AppOnly), new NullEvidenceSink(), CancellationToken.None);

        Assert.Equal(SectionCoverage.Skipped, result.Coverage);
        Assert.Contains("T-04", Assert.Single(result.Warnings));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Disabled_PoC_collector_reports_confidence_without_calling_endpoint()
    {
        var client = new FakeBapClient();
        var collector = new DlpPolicyCollector(new CollectorFeatureOptions(), client);

        var result = await collector.CollectAsync(Context(SnapshotMode.Delegated), new NullEvidenceSink(), CancellationToken.None);

        Assert.Equal(SectionCoverage.Skipped, result.Coverage);
        Assert.Contains("PocRequired", Assert.Single(result.Warnings));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Enabled_unverified_collector_never_claims_full()
    {
        var options = new CollectorFeatureOptions
        {
            Sections = new()
            {
                ["dlpPolicies"] = new() { Enabled = true, Route = new Uri("https://verified-by-operator.test/dlp") }
            }
        };
        var client = new FakeBapClient();
        var collector = new DlpPolicyCollector(options, client);

        var result = await collector.CollectAsync(Context(SnapshotMode.Delegated), new NullEvidenceSink(), CancellationToken.None);

        Assert.Equal(SectionCoverage.Partial, result.Coverage);
        Assert.Contains(result.Warnings, warning => warning.Contains("PocRequired", StringComparison.Ordinal));
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task Selected_section_scope_downgrades_documented_collector_to_partial()
    {
        var client = new FakeBapClient();
        var collector = new EnvironmentsCollector(client);

        var result = await collector.CollectAsync(
            Context(SnapshotMode.Delegated) with { RequestedSections = ["environments"] },
            new NullEvidenceSink(), CancellationToken.None);

        Assert.Equal(SectionCoverage.Partial, result.Coverage);
        Assert.Contains(result.Warnings, warning => warning.Contains("subset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Selected_environment_scope_downgrades_documented_collector_to_partial()
    {
        var collector = new EnvironmentsCollector(new FakeBapClient());

        var result = await collector.CollectAsync(
            Context(SnapshotMode.Delegated) with { ExpectedEnvironmentIds = ["environment-1"] },
            new NullEvidenceSink(), CancellationToken.None);

        Assert.Equal(SectionCoverage.Partial, result.Coverage);
        Assert.Contains(result.Warnings, warning => warning.Contains("environment subset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Delegated_token_provider_forwards_authenticated_principal()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([], "fixture"));
        var delegated = new CapturingDelegatedAcquirer();
        var provider = new CollectorTokenProvider(delegated, new NeverAppOnlyAcquirer());

        await provider.GetAccessTokenAsync(new(
            Guid.NewGuid(), SnapshotMode.Delegated, CollectorResources.Graph, "basis", principal),
            CancellationToken.None);

        Assert.Same(principal, delegated.Principal);
    }

    private static SnapshotCollectorContext Context(SnapshotMode mode) => new(
        new TenantContext(Guid.NewGuid(), SubjectIdentity.Create(Guid.NewGuid(), Guid.NewGuid()), MembershipRole.CustomerAdmin),
        Guid.NewGuid(), Guid.NewGuid(), mode, "identity",
        new ClaimsPrincipal(new ClaimsIdentity([], "fixture")), CollectorConfidence.PocRequired);

    private sealed class CapturingDelegatedAcquirer : IDelegatedOboTokenAcquirer
    {
        public ClaimsPrincipal? Principal { get; private set; }
        public ValueTask<string> GetAccessTokenAsync(Guid entraTenantId, string resourceScope, ClaimsPrincipal authenticatedPrincipal, CancellationToken cancellationToken)
        {
            Principal = authenticatedPrincipal;
            return ValueTask.FromResult("token");
        }
    }

    private sealed class NeverAppOnlyAcquirer : IAppOnlyTokenAcquirer
    {
        public ValueTask<string> GetAccessTokenAsync(Guid entraTenantId, string resourceScope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("App-only should not be used by this test.");
    }

    private sealed class FakeBapClient : IBapApiClient
    {
        public int Calls { get; private set; }
        public Task<PagedCollectionResult> CollectGetAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) => Result();
        public Task<PagedCollectionResult> CollectPostAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) => Result();
        private Task<PagedCollectionResult> Result()
        {
            Calls++;
            return Task.FromResult(new PagedCollectionResult(SectionCoverage.Full, 1, [], []));
        }
    }

    private sealed class NullEvidenceSink : ISnapshotEvidenceSink
    {
        public ValueTask WriteRawAsync(RawEvidenceReference evidence, Stream content, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}