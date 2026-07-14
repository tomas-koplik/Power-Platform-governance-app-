using System.Text.Json;
using System.Security.Cryptography;
using Ppgsm.Core.Domain;

namespace Ppgsm.Core.Tests;

public sealed class RuleEvaluationTests
{
    private const string Version = "1.0.0";
    private const string Attestation = "ppgsm-rules-1.0.0-reviewed-20260714";

    [Fact]
    public async Task Every_acceptance_fixture_executes_through_the_real_evaluator_runtime()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "rules", "v1");
        var digest = WriteManifest(Path.Combine(root, "catalog.yaml"), Path.Combine(root, "default-profile.yaml"));
        var published = await new FilePublishedRuleCatalog(
            Path.Combine(root, "catalog.yaml"), Path.Combine(root, "default-profile.yaml"), Version, digest)
            .GetCurrentAsync(CancellationToken.None);
        Assert.NotNull(published);
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fixtures", "acceptance-cases.yaml")));
        var runtime = new RuleEvaluationRuntime(new());

        foreach (var fixture in fixtures.RootElement.GetProperty("cases").EnumerateArray())
        {
            var ruleId = fixture.GetProperty("ruleId").GetString()!;
            var profileMode = fixture.GetProperty("profileMode").GetString()!;
            var profile = published.DefaultProfile with
            {
                Rules = published.DefaultProfile.Rules.Select(value => value.RuleId == ruleId ? value with { Mode = profileMode } : value).ToArray()
            };
            var ruleSet = published with { DefaultProfile = profile };
            var evidence = Evidence(fixture, ruleSet.Rules.Single(value => value.Id == ruleId));
            var activeExceptions = fixture.TryGetProperty("activeException", out var active) && active.GetBoolean()
                ? new HashSet<string>([ruleId], StringComparer.Ordinal) : null;
            var findings = runtime.Evaluate(new(Guid.NewGuid(), Guid.NewGuid(), 1, ruleSet, evidence,
                new HashSet<string>([ruleId], StringComparer.Ordinal), activeExceptions));
            var finding = findings.Single(value => value.RuleId == ruleId);

            Assert.Equal(fixture.GetProperty("expectedStatus").GetString(), finding.Status.ToString());
            if (fixture.TryGetProperty("evaluatorRatio", out var expectedRatio)) Assert.Equal(expectedRatio.GetDecimal(), finding.EvaluatorRatio);
            var score = GovernanceScoring.Calculate(finding.CustomerId, finding.SnapshotId, [finding], EvidenceCoverageAggregator.Aggregate(evidence.Values.Select(value => value.Coverage)));
            Assert.Equal(fixture.GetProperty("scored").GetBoolean(), score.Evaluated == 1);
        }
    }

    [Theory]
    [InlineData(SectionCoverage.Full, SectionCoverage.Full, SectionCoverage.Full)]
    [InlineData(SectionCoverage.Full, SectionCoverage.Partial, SectionCoverage.Partial)]
    [InlineData(SectionCoverage.Partial, SectionCoverage.Failed, SectionCoverage.Partial)]
    [InlineData(SectionCoverage.Failed, SectionCoverage.Skipped, SectionCoverage.Failed)]
    [InlineData(SectionCoverage.Skipped, SectionCoverage.Skipped, SectionCoverage.Skipped)]
    public void Coverage_aggregator_is_deterministic(SectionCoverage first, SectionCoverage second, SectionCoverage expected) =>
        Assert.Equal(expected, EvidenceCoverageAggregator.Aggregate([first, second]));

    [Fact]
    public void Zero_denominator_has_not_evaluated_tier()
    {
        var customerId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var finding = new Finding(Guid.NewGuid(), customerId, snapshotId, "PPG-X-001", "Advisory", "Area",
            FindingSeverity.Informational, FindingStatus.NotEvaluated, "Tenant", "Unknown", "Review", "Review", RemediationKind.Informational);

        var score = GovernanceScoring.Calculate(customerId, snapshotId, [finding], SectionCoverage.Skipped);

        Assert.Equal("Not evaluated", score.Tier);
        Assert.Equal(0, score.Evaluated);
    }

    [Fact]
    public void Legacy_section_aliases_are_read_but_new_key_is_canonical()
    {
        Assert.Equal(SectionKeys.DlpPolicies, SectionKeys.Canonicalize("dlp"));
        Assert.Contains(SectionKeys.DlpPolicies, SectionKeys.Canonical);
        Assert.DoesNotContain("dlp", SectionKeys.Canonical);
    }

    [Fact]
    public void Poc_approval_requires_exact_tenant_rule_identity_api_version_and_is_inactive_at_expiry()
    {
        var now = DateTimeOffset.Parse("2026-07-14T12:00:00Z");
        var customerId = Guid.NewGuid();
        var approval = new PocApproval(Guid.NewGuid(), customerId, "PPG-DLP-002", "delegated:reader", "2026-04-01",
            Guid.NewGuid(), "external-approver", now.AddHours(-1), now.AddHours(1));

        Assert.True(approval.Matches(customerId, "PPG-DLP-002", "delegated:reader", "2026-04-01", now));
        Assert.False(approval.Matches(Guid.NewGuid(), "PPG-DLP-002", "delegated:reader", "2026-04-01", now));
        Assert.False(approval.Matches(customerId, "PPG-DLP-002", "app-only:reader", "2026-04-01", now));
        Assert.False(approval.Matches(customerId, "PPG-DLP-002", "delegated:reader", "2025-01-01", now));
        Assert.False(approval.Matches(customerId, "PPG-DLP-002", "delegated:reader", "2026-04-01", approval.ExpiresAt));
    }

    [Theory]
    [InlineData("disabled", "always", "default")]
    [InlineData("enabled", "resource-present", "apps")]
    public void Profile_and_resource_absence_are_not_applicable(string profileMode, string applicabilityMode, string profileKey)
    {
        using var parameters = JsonDocument.Parse("{}");
        var rule = new RuleDefinition("PPG-ENV-999", 1, "Applicability", "Environments", FindingSeverity.Medium, 1m,
            new(applicabilityMode, profileKey), new("dlp.coverage", 1, parameters.RootElement.Clone()), 1,
            [new(SectionKeys.Environments, "Full", "Documented", ["environments[*].id"])], "Rationale", "Recommendation",
            new(false, null, false, "Review"), new(RemediationKind.Manual, "Guidance", "Tenant", ["Approval"], "Verify", "Rollback"),
            "https://example.test", new("Documented", "None"));
        var publication = new PublishedRuleSet("1", DateTimeOffset.UtcNow, "reviewed", [rule],
            new("default", 1, [new(rule.Id, profileMode, "profile decision")]), "sha256:" + new string('a', 64), "{\"dlp.coverage\":1}");
        using var environments = JsonDocument.Parse("[]");
        var sections = new Dictionary<string, EvaluationEvidenceSection>(StringComparer.Ordinal)
        {
            [SectionKeys.Environments] = new(SectionKeys.Environments, SectionCoverage.Full, EvidenceConfidence.Documented,
                environments.RootElement.Clone(), [Guid.NewGuid()])
        };

        var finding = Assert.Single(new RuleEvaluationRuntime(new()).Evaluate(
            new(Guid.NewGuid(), Guid.NewGuid(), 1, publication, sections)));

        Assert.Equal(FindingStatus.NotApplicable, finding.Status);
    }

    private static string WriteManifest(string catalogPath, string profilePath)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            catalogSha256 = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(catalogPath)))}",
            profileSha256 = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(profilePath)))}"
        });
        File.WriteAllBytes(catalogPath + ".manifest.json", manifest);
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(manifest))}";
    }

    private static IReadOnlyDictionary<string, EvaluationEvidenceSection> Evidence(JsonElement fixture, RuleDefinition rule)
    {
        var coverage = Enum.Parse<SectionCoverage>(fixture.GetProperty("evidenceCoverage").GetString()!);
        var documents = Documents(fixture);
        return rule.EvidenceRequirements.ToDictionary(requirement => SectionKeys.Canonicalize(requirement.Section), requirement =>
        {
            var key = SectionKeys.Canonicalize(requirement.Section);
            return new EvaluationEvidenceSection(key, coverage, Enum.Parse<EvidenceConfidence>(requirement.Confidence),
                documents[key].RootElement.Clone(), [Guid.NewGuid()]);
        }, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, JsonDocument> Documents(JsonElement fixture)
    {
        var observed = fixture.TryGetProperty("observed", out var observedValue) && observedValue.GetBoolean();
        var measured = fixture.TryGetProperty("measured", out var measuredValue) ? measuredValue.GetDecimal() : 1m;
        var covered = (int)(4 * measured);
        var ownerStatus = fixture.TryGetProperty("ownerStatus", out var ownerValue) ? ownerValue.GetString() : "Enabled";
        var noProduction = fixture.TryGetProperty("productionEnvironmentCount", out var count) && count.GetInt32() == 0;
        return new Dictionary<string, JsonDocument>(StringComparer.Ordinal)
        {
            [SectionKeys.TenantSettings] = JsonDocument.Parse($$"""{"disableEnvironmentCreationByNonAdminUsers":{{observed.ToString().ToLowerInvariant()}},"generativeAiDataMovement":"Unknown"}"""),
            [SectionKeys.Environments] = JsonDocument.Parse(noProduction
                ? "[{\"id\":\"dev\",\"environmentType\":\"Developer\",\"isDefault\":false,\"governanceConfiguration\":{\"protectionLevel\":\"Disabled\"}}]"
                : "[{\"id\":\"e1\",\"environmentType\":\"Production\",\"isDefault\":true,\"governanceConfiguration\":{\"protectionLevel\":\"Enabled\"}},{\"id\":\"e2\",\"environmentType\":\"Sandbox\",\"isDefault\":false,\"governanceConfiguration\":{\"protectionLevel\":\"Disabled\"}},{\"id\":\"e3\",\"environmentType\":\"Sandbox\",\"isDefault\":false,\"governanceConfiguration\":{\"protectionLevel\":\"Disabled\"}},{\"id\":\"e4\",\"environmentType\":\"Sandbox\",\"isDefault\":false,\"governanceConfiguration\":{\"protectionLevel\":\"Disabled\"}}]"),
            [SectionKeys.DlpPolicies] = JsonDocument.Parse($$"""[{"environmentType":"Selected","environments":[{{string.Join(',', Enumerable.Range(1, covered).Select(value => $"{{\"id\":\"e{value}\"}}"))}}]}]"""),
            [SectionKeys.Apps] = JsonDocument.Parse("[{\"environmentId\":\"e1\",\"ownerObjectId\":\"owner-1\",\"roleAssignments\":[]}]"),
            [SectionKeys.Flows] = JsonDocument.Parse("[{\"environmentId\":\"e1\",\"ownerObjectId\":\"owner-1\",\"roleAssignments\":[]}]"),
            [SectionKeys.OwnerDirectory] = JsonDocument.Parse($$"""[{"objectId":"owner-1","status":"{{ownerStatus}}"}]""")
        };
    }
}