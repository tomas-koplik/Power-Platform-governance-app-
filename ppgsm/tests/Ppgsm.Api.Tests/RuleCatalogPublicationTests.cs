using Ppgsm.Api;
using Ppgsm.Core.Domain;
using System.Text.Json;

namespace Ppgsm.Api.Tests;

public sealed class RuleCatalogPublicationTests
{
    private const string Version = "1.0.0";
    private const string Attestation = "ppgsm-rules-1.0.0-reviewed-20260714";

    [Fact]
    public async Task Publishes_reviewed_catalog_when_contract_version_and_attestation_match()
    {
        var catalog = Catalog();

        var published = await catalog.GetCurrentAsync(CancellationToken.None);

        Assert.NotNull(published);
        Assert.Equal(Version, published.Version);
        Assert.Equal(6, published.Rules.Count);
        Assert.Equal(6, published.DefaultProfile.Rules.Count);
    }

    [Theory]
    [InlineData("0.9.0", Attestation)]
    [InlineData(Version, "untrusted")]
    [InlineData(Version, "")]
    public async Task Fails_closed_when_version_or_attestation_is_not_trusted(string version, string attestation)
    {
        var published = await Catalog(version, attestation).GetCurrentAsync(CancellationToken.None);

        Assert.Null(published);
    }

    [Fact]
    public async Task Fails_closed_for_malformed_catalog()
    {
        var malformed = Path.GetTempFileName();
        await File.WriteAllTextAsync(malformed, "not-json-compatible-yaml");
        try
        {
            var published = await new FilePublishedRuleCatalog(malformed, ProfilePath(), Version, Attestation)
                .GetCurrentAsync(CancellationToken.None);
            Assert.Null(published);
        }
        finally
        {
            File.Delete(malformed);
        }
    }

    [Fact]
    public async Task Fails_closed_when_default_profile_omits_a_rule()
    {
        var incomplete = Path.GetTempFileName();
        await File.WriteAllTextAsync(incomplete, "{\"id\":\"default\",\"version\":1,\"rules\":[]}");
        try
        {
            var published = await new FilePublishedRuleCatalog(CatalogPath(), incomplete, Version, Attestation)
                .GetCurrentAsync(CancellationToken.None);
            Assert.Null(published);
        }
        finally
        {
            File.Delete(incomplete);
        }
    }

    [Theory]
    [InlineData("\"key\": \"dlp.coverage\", \"version\": 1", "\"key\": \"unknown.evaluator\", \"version\": 1")]
    [InlineData("\"key\": \"dlp.coverage\", \"version\": 1", "\"key\": \"dlp.coverage\", \"version\": 2")]
    public async Task Fails_closed_for_unknown_or_mismatched_evaluator(string current, string replacement)
    {
        var invalid = Path.GetTempFileName();
        await File.WriteAllTextAsync(invalid, (await File.ReadAllTextAsync(CatalogPath())).Replace(current, replacement, StringComparison.Ordinal));
        try
        {
            var published = await new FilePublishedRuleCatalog(invalid, ProfilePath(), Version, Attestation).GetCurrentAsync(CancellationToken.None);
            Assert.Null(published);
            Assert.False(new ApiCapabilityRegistry(new FilePublishedRuleCatalog(invalid, ProfilePath(), Version, Attestation)).Status["Score"]);
        }
        finally
        {
            File.Delete(invalid);
        }
    }

    [Fact]
    public void Score_capability_fails_closed_without_a_published_catalog()
    {
        var capabilities = new ApiCapabilityRegistry(new NoPublishedRuleCatalog());

        Assert.False(capabilities.Status["Score"]);
        Assert.Throws<CapabilityUnavailableException>(() => capabilities.Require(ApiCapability.Score));
    }

    [Fact]
    public void Score_capability_is_enabled_for_a_validated_publication()
    {
        var published = Catalog().GetCurrentAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Assert.NotNull(published);
        var capabilities = new ApiCapabilityRegistry(new StaticCatalog(published));

        Assert.True(capabilities.Status["Score"]);
    }

    [Fact]
    public async Task Acceptance_fixtures_cover_every_outcome_and_exclude_non_scoring_states()
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "rules", "v1", "fixtures", "acceptance-cases.yaml"));
        using var document = await JsonDocument.ParseAsync(stream);
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var statuses = cases.Select(value => value.GetProperty("expectedStatus").GetString()).ToHashSet(StringComparer.Ordinal);

        Assert.Subset(statuses, new HashSet<string>(["Pass", "Fail", "Partial", "NotEvaluated", "NotApplicable", "Excepted"], StringComparer.Ordinal));
        Assert.All(cases.Where(value => value.GetProperty("expectedStatus").GetString() is "NotEvaluated" or "NotApplicable" or "Excepted"),
            value => Assert.False(value.GetProperty("scored").GetBoolean()));
    }

    private static FilePublishedRuleCatalog Catalog(string version = Version, string attestation = Attestation) =>
        new(CatalogPath(), ProfilePath(), version, attestation);

    private static string CatalogPath() => Path.Combine(AppContext.BaseDirectory, "rules", "v1", "catalog.yaml");
    private static string ProfilePath() => Path.Combine(AppContext.BaseDirectory, "rules", "v1", "default-profile.yaml");

    private sealed class StaticCatalog(PublishedRuleSet published) : IPublishedRuleCatalog
    {
        public ValueTask<PublishedRuleSet?> GetCurrentAsync(CancellationToken cancellationToken) => ValueTask.FromResult<PublishedRuleSet?>(published);
    }
}