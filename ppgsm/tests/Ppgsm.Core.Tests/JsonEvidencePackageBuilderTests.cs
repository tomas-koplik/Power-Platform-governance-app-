using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ppgsm.Core.Domain;
using Ppgsm.Worker;

namespace Ppgsm.Core.Tests;

public sealed class JsonEvidencePackageBuilderTests
{
    private static readonly Guid CustomerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SnapshotId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ExportId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid EvidenceId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Rejects_cross_tenant_snapshot_substitution()
    {
        var input = Input(snapshotCustomerId: Guid.Parse("99999999-9999-9999-9999-999999999999"));

        await Assert.ThrowsAsync<TenantAccessDeniedException>(async () =>
            await BuildAsync(input));
    }

    [Fact]
    public async Task Recursively_redacts_identity_fields_and_omits_requester()
    {
        var json = await BuildAsync(Input());

        Assert.DoesNotContain("Ada Lovelace", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ada@example.test", json, StringComparison.Ordinal);
        Assert.DoesNotContain("requester-subject", json, StringComparison.Ordinal);
        Assert.Contains("retained", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var package = document.RootElement.GetProperty("evidencePackage");
        Assert.Equal("[REDACTED]", package.GetProperty("normalizedEvidence").GetProperty("environments")[0].GetProperty("displayName").GetString());
        Assert.False(package.GetProperty("rawEvidence")[0].TryGetProperty("body", out _));
    }

    [Fact]
    public async Task Not_evaluated_findings_omit_score()
    {
        var input = Input(findings:
        [
            Finding(FindingStatus.NotEvaluated)
        ]);

        using var document = JsonDocument.Parse(await BuildAsync(input));

        Assert.False(document.RootElement.GetProperty("evidencePackage").TryGetProperty("score", out _));
    }

    [Fact]
    public async Task Partial_section_coverage_is_preserved()
    {
        using var document = JsonDocument.Parse(await BuildAsync(Input(partialCoverage: true)));
        var sections = document.RootElement.GetProperty("evidencePackage").GetProperty("snapshot").GetProperty("sections");

        Assert.Contains(sections.EnumerateArray(), section =>
            section.GetProperty("sectionKey").GetString() == SectionKeys.Environments &&
            section.GetProperty("coverage").GetString() == nameof(SectionCoverage.Partial));
    }

    [Fact]
    public async Task Deterministic_input_produces_identical_bytes_and_hash()
    {
        var first = await BuildBytesAsync(Input());
        var second = await BuildBytesAsync(Input());

        Assert.Equal(first, second);
        using var firstDocument = JsonDocument.Parse(first);
        using var secondDocument = JsonDocument.Parse(second);
        Assert.Equal(firstDocument.RootElement.GetProperty("contentHash").GetString(), secondDocument.RootElement.GetProperty("contentHash").GetString());
    }

    [Fact]
    public async Task Complete_package_round_trips_and_content_hash_covers_authoritative_payload()
    {
        var bytes = await BuildBytesAsync(Input(findings: [Finding(FindingStatus.Fail)]));
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var package = root.GetProperty("evidencePackage");
        var calculated = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package.GetRawText()))).ToLowerInvariant();

        Assert.Equal($"sha256:{calculated}", root.GetProperty("contentHash").GetString());
        Assert.True(package.TryGetProperty("snapshot", out _));
        Assert.True(package.TryGetProperty("ruleCatalog", out _));
        Assert.True(package.TryGetProperty("normalizedEvidence", out var normalized));
        Assert.Single(normalized.GetProperty("tenantSettings").EnumerateArray());
        Assert.Single(normalized.GetProperty("environments").EnumerateArray());
        Assert.Single(normalized.GetProperty("dlpPolicies").EnumerateArray());
        Assert.Single(package.GetProperty("findings").EnumerateArray());
        Assert.Single(package.GetProperty("rawEvidence").EnumerateArray());
        Assert.True(package.TryGetProperty("score", out _));
        Assert.Equal(EvidenceId, package.GetProperty("findings")[0].GetProperty("evidenceReferences")[0].GetProperty("evidenceId").GetGuid());
    }

    [Fact]
    public async Task Legacy_section_alias_is_exported_canonically_with_original_lineage()
    {
        var input = Input();
        input = input with { RawEvidence = [Evidence() with { SectionKey = "tenant-settings" }] };

        using var document = JsonDocument.Parse(await BuildAsync(input));
        var raw = document.RootElement.GetProperty("evidencePackage").GetProperty("rawEvidence")[0];

        Assert.Equal(SectionKeys.TenantSettings, raw.GetProperty("sectionKey").GetString());
        Assert.Equal("tenant-settings", raw.GetProperty("originalSectionKey").GetString());
    }

    private static async Task<string> BuildAsync(JsonEvidencePackageInput input) =>
        Encoding.UTF8.GetString(await BuildBytesAsync(input));

    private static async Task<byte[]> BuildBytesAsync(JsonEvidencePackageInput input)
    {
        var builder = new JsonEvidencePackageBuilder(new(1024 * 1024));
        await using var stream = new MemoryStream();
        await builder.WriteAsync(input, stream, CancellationToken.None);
        return stream.ToArray();
    }

    private static JsonEvidencePackageInput Input(
        Guid? snapshotCustomerId = null,
        bool partialCoverage = false,
        IReadOnlyCollection<Finding>? findings = null)
    {
        var snapshot = Snapshot(snapshotCustomerId ?? CustomerId, partialCoverage);
        var job = new ExportJob(ExportId, CustomerId, ExportFormat.Json, ExportJobStatus.Running, Timestamp,
            "requester-subject", SnapshotId: SnapshotId);
        using var knownSettings = JsonDocument.Parse("""{"safe":"retained","nested":{"userPrincipalName":"ada@example.test"}}""");
        using var environmentProperties = JsonDocument.Parse("""{"owner":{"displayName":"Ada Lovelace"},"safe":"retained"}""");
        using var dlpProperties = JsonDocument.Parse("""{"nested":{"mail":"ada@example.test"},"safe":"retained"}""");
        var tenantSettings = new TenantSettingsEvidence(Guid.Parse("55555555-5555-5555-5555-555555555555"), CustomerId, SnapshotId,
            true, false, null, JsonDocument.Parse(knownSettings.RootElement.GetRawText()), EvidenceId);
        var environment = new EnvironmentEvidence(Guid.Parse("66666666-6666-6666-6666-666666666666"), CustomerId, SnapshotId,
            "env-1", "Ada Lovelace", "Production", "EU", true, true, "Standard", true, null,
            JsonDocument.Parse(environmentProperties.RootElement.GetRawText()), EvidenceId);
        var dlp = new DlpPolicyEvidence(Guid.Parse("77777777-7777-7777-7777-777777777777"), CustomerId, SnapshotId,
            "dlp-1", "Ada Lovelace", JsonDocument.Parse(dlpProperties.RootElement.GetRawText()), EvidenceId);
        return new(job, snapshot, Catalog(), [tenantSettings], [environment], [dlp], [Evidence()],
            new Dictionary<Guid, byte[]>(), findings ?? [Finding(FindingStatus.Pass)], Timestamp);
    }

    private static Snapshot Snapshot(Guid customerId, bool partialCoverage)
    {
        var snapshot = new Snapshot(SnapshotId, customerId, "idempotency", "captured-requester", SnapshotMode.AppOnly, 3, Timestamp.AddMinutes(-5));
        snapshot.Start(Timestamp.AddMinutes(-4));
        snapshot.RecordSection(new(Guid.Parse("88888888-8888-8888-8888-888888888881"), customerId, SnapshotId,
            SectionKeys.TenantSettings, SectionCoverage.Full, 1, null, Timestamp.AddMinutes(-2)));
        snapshot.RecordSection(new(Guid.Parse("88888888-8888-8888-8888-888888888882"), customerId, SnapshotId,
            SectionKeys.Environments, partialCoverage ? SectionCoverage.Partial : SectionCoverage.Full, 1, null, Timestamp.AddMinutes(-2)));
        snapshot.RecordSection(new(Guid.Parse("88888888-8888-8888-8888-888888888883"), customerId, SnapshotId,
            SectionKeys.DlpPolicies, SectionCoverage.Full, 1, null, Timestamp.AddMinutes(-2)));
        snapshot.Complete(Timestamp.AddMinutes(-1));
        return snapshot;
    }

    private static PublishedRuleSet Catalog()
    {
        var rule = new RuleDefinition("PPG-TS-001", 4, "Tenant control", "Tenant", FindingSeverity.High, 1m,
            new("always", "default"), new("boolean", 7, JsonDocument.Parse("{}").RootElement.Clone()), 1,
            [new(SectionKeys.TenantSettings, "Full", "Documented", ["$.safe"])], "Rationale", "Recommendation",
            new(true, 30, true, "Review"), new(RemediationKind.Manual, "Guidance", "Tenant", ["Approval"], "Verify", "Manual rollback"),
            "https://example.test/docs", new("verified", "Question"));
        return new("2026.07", Timestamp.AddDays(-1), "approved-attestation", [rule],
            new("default", 2, [new(rule.Id, "enabled", "baseline")]), "sha256:" + new string('a', 64), "{\"boolean\":7}");
    }

    private static Finding Finding(FindingStatus status) => new(
        Guid.Parse("99999999-9999-9999-9999-999999999991"), CustomerId, SnapshotId, "PPG-TS-001", "Tenant control", "Tenant",
        FindingSeverity.High, status, "Tenant", "Observed", "Interpretation", "Action", RemediationKind.Manual,
        RuleVersion: 4, CatalogVersion: "2026.07", EvaluatorKey: "boolean", EvaluatorVersion: 7,
        EvidenceLinksJson: $$"""[{"evidenceId":"{{EvidenceId:D}}","path":"$.safe"}]""",
        PublicationContentDigest: "sha256:" + new string('a', 64), EvaluatorVersionsJson: "{\"boolean\":7}");

    private static RawEvidenceReference Evidence() => new(
        EvidenceId, CustomerId, SnapshotId, SectionKeys.TenantSettings, "immutable/path.json", "sha256:source", "application/json",
        "2026-04-01", Timestamp.AddMinutes(-3), EvidenceConfidence.Documented, "tenant-settings", "2.1", "3", null, "GET",
        "https://example.test/redacted", 200, "resource", SnapshotMode.AppOnly, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "ada@example.test", "request-id", new Dictionary<string, string> { ["Authorization"] = "[REDACTED]" }, 1, 1, null, "Complete page.");
}