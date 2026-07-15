using Ppgsm.Api;
using Ppgsm.Core.Domain;
using System.Security.Cryptography;
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
    [InlineData("0.9.0", false)]
    [InlineData(Version, true)]
    public async Task Fails_closed_when_version_or_manifest_digest_is_not_trusted(string version, bool untrustedDigest)
    {
        var published = await Catalog(version, untrustedDigest ? "sha256:" + new string('0', 64) : null).GetCurrentAsync(CancellationToken.None);

        Assert.Null(published);
    }

    [Fact]
    public async Task Fails_closed_when_catalog_bytes_change_after_manifest_publication()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var catalogPath = Path.Combine(directory.FullName, "catalog.yaml");
            var profilePath = Path.Combine(directory.FullName, "default-profile.yaml");
            File.Copy(CatalogPath(), catalogPath);
            File.Copy(ProfilePath(), profilePath);
            var digest = WriteManifest(catalogPath, profilePath);
            await File.AppendAllTextAsync(catalogPath, " ");

            Assert.Null(await new FilePublishedRuleCatalog(catalogPath, profilePath, Version, digest).GetCurrentAsync(CancellationToken.None));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Resolves_retained_v1_digest_after_v2_is_current()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var v1 = Directory.CreateDirectory(Path.Combine(root.FullName, "v1"));
            var v2 = Directory.CreateDirectory(Path.Combine(root.FullName, "v2"));
            File.Copy(CatalogPath(), Path.Combine(v1.FullName, "catalog.yaml"));
            File.Copy(ProfilePath(), Path.Combine(v1.FullName, "default-profile.yaml"));
            var v2Catalog = (await File.ReadAllTextAsync(CatalogPath())).Replace("\"1.0.0\"", "\"2.0.0\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(v2.FullName, "catalog.yaml"), v2Catalog);
            File.Copy(ProfilePath(), Path.Combine(v2.FullName, "default-profile.yaml"));
            var v1Digest = WriteManifest(Path.Combine(v1.FullName, "catalog.yaml"), Path.Combine(v1.FullName, "default-profile.yaml"));
            var v2Digest = WriteManifest(Path.Combine(v2.FullName, "catalog.yaml"), Path.Combine(v2.FullName, "default-profile.yaml"));
            var catalog = new FilePublishedRuleCatalog(Path.Combine(v2.FullName, "catalog.yaml"), Path.Combine(v2.FullName, "default-profile.yaml"),
                "2.0.0", $"{v2Digest};{v1Digest}");

            var retained = await catalog.GetByDigestAsync(v1Digest, CancellationToken.None);

            Assert.NotNull(retained);
            Assert.Equal(Version, retained.Version);
            Assert.Equal(v1Digest, retained.ContentDigest);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Fails_closed_for_malformed_catalog()
    {
        var malformed = Path.GetTempFileName();
        await File.WriteAllTextAsync(malformed, "not-json-compatible-yaml");
        try
        {
            var published = await new FilePublishedRuleCatalog(malformed, ProfilePath(), Version, WriteManifest(malformed, ProfilePath()))
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
            var published = await new FilePublishedRuleCatalog(CatalogPath(), incomplete, Version, WriteManifest(CatalogPath(), incomplete))
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
            var digest = WriteManifest(invalid, ProfilePath());
            var published = await new FilePublishedRuleCatalog(invalid, ProfilePath(), Version, digest).GetCurrentAsync(CancellationToken.None);
            Assert.Null(published);
            Assert.False(new ApiCapabilityRegistry(new FilePublishedRuleCatalog(invalid, ProfilePath(), Version, digest),
                new BuiltInTrustedRemediationTemplateCatalog(), new OffboardingCapability(false)).Status["Score"]);
        }
        finally
        {
            File.Delete(invalid);
        }
    }

    [Fact]
    public void Score_capability_fails_closed_without_a_published_catalog()
    {
        var capabilities = new ApiCapabilityRegistry(new NoPublishedRuleCatalog(),
            new BuiltInTrustedRemediationTemplateCatalog(), new OffboardingCapability(false));

        Assert.False(capabilities.Status["Score"]);
        Assert.Throws<CapabilityUnavailableException>(() => capabilities.Require(ApiCapability.Score));
    }

    [Fact]
    public async Task Score_capability_is_enabled_for_a_validated_publication()
    {
        var published = await Catalog().GetCurrentAsync(CancellationToken.None);
        Assert.NotNull(published);
        var capabilities = new ApiCapabilityRegistry(new StaticCatalog(published),
            new BuiltInTrustedRemediationTemplateCatalog(), new OffboardingCapability(false));

        Assert.True(capabilities.Status["Score"]);
    }

    [Fact]
    public async Task Acceptance_fixtures_cover_every_outcome_and_exclude_non_scoring_states()
    {
        await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "rules", "v1", "fixtures", "acceptance-cases.yaml"));
        using var document = await JsonDocument.ParseAsync(stream);
        var cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var statuses = cases.Select(value => value.GetProperty("expectedStatus").GetString()!).ToHashSet(StringComparer.Ordinal);

        Assert.Subset(statuses, new HashSet<string>(["Pass", "Fail", "Partial", "NotEvaluated", "NotApplicable", "Excepted"], StringComparer.Ordinal));
        Assert.All(cases.Where(value => value.GetProperty("expectedStatus").GetString() is "NotEvaluated" or "NotApplicable" or "Excepted"),
            value => Assert.False(value.GetProperty("scored").GetBoolean()));
    }

    private static FilePublishedRuleCatalog Catalog(string version = Version, string? manifestDigest = null) =>
        new(CatalogPath(), ProfilePath(), version, manifestDigest ?? WriteManifest(CatalogPath(), ProfilePath()));

    private static string WriteManifest(string catalogPath, string profilePath)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            catalogSha256 = $"sha256:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogPath))).ToLowerInvariant()}",
            profileSha256 = $"sha256:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profilePath))).ToLowerInvariant()}"
        });
        File.WriteAllBytes(catalogPath + ".manifest.json", manifest);
        return $"sha256:{Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant()}";
    }

    private static string CatalogPath() => Path.Combine(AppContext.BaseDirectory, "rules", "v1", "catalog.yaml");
    private static string ProfilePath() => Path.Combine(AppContext.BaseDirectory, "rules", "v1", "default-profile.yaml");

    private sealed class StaticCatalog(PublishedRuleSet published) : IPublishedRuleCatalog
    {
        public ValueTask<PublishedRuleSet?> GetCurrentAsync(CancellationToken cancellationToken) => ValueTask.FromResult<PublishedRuleSet?>(published);
        public ValueTask<PublishedRuleSet?> GetByDigestAsync(string contentDigest, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PublishedRuleSet?>(contentDigest == published.ContentDigest ? published : null);
    }
}