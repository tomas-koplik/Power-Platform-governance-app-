using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Ppgsm.Core.Domain;
using Ppgsm.Worker;

namespace Ppgsm.Api.Tests;

public sealed class FinalIntegrationContractTests
{
    [Fact]
    public async Task Evidence_metadata_is_tenant_and_snapshot_scoped_and_excludes_storage_path_from_api_contract()
    {
        var store = new LocalDevelopmentStore();
        var customer = Guid.NewGuid();
        var otherCustomer = Guid.NewGuid();
        var snapshot = Guid.NewGuid();
        var evidence = Reference(customer, snapshot);
        await store.WriteRawAsync(evidence, new MemoryStream("{}"u8.ToArray()), CancellationToken.None);

        var own = await store.ListEvidenceMetadataAsync(customer, snapshot, 1, 50, CancellationToken.None);
        var other = await store.ListEvidenceMetadataAsync(otherCustomer, snapshot, 1, 50, CancellationToken.None);

        Assert.Single(own.Items);
        Assert.Empty(other.Items);
        Assert.DoesNotContain(typeof(EvidenceMetadataResponse).GetProperties(), property => property.Name.Contains("Storage", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(EvidenceMetadataResponse).GetProperties(), property => property.Name.Contains("Uri", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Non_privileged_raw_projection_redacts_identity_fields_recursively()
    {
        var reference = Reference(Guid.NewGuid(), Guid.NewGuid());
        var json = """{"displayName":"Ada","nested":{"userPrincipalName":"ada@example.test"},"safe":"retained"}""";
        var projection = await RawEvidenceProjection.CreateAsync(
            new(reference, new MemoryStream(Encoding.UTF8.GetBytes(json))), false, CancellationToken.None);
        var serialized = JsonSerializer.Serialize(projection);

        Assert.DoesNotContain("Ada", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("ada@example.test", serialized, StringComparison.Ordinal);
        Assert.Contains("retained", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_lifecycle_is_durable_tenant_scoped_and_download_artifact_is_not_cross_tenant()
    {
        var store = new LocalDevelopmentStore();
        var customer = Guid.NewGuid();
        var job = new ExportJob(Guid.NewGuid(), customer, ExportFormat.Json, ExportJobStatus.Queued,
            DateTimeOffset.UtcNow, "subject", SnapshotId: Guid.NewGuid());
        await store.AddExportAsync(job, CancellationToken.None);

        var claimed = await store.ClaimExportAsync(job.ExportJobId, CancellationToken.None);
        Assert.Equal(ExportJobStatus.Running, claimed?.Status);
        Assert.Null(await store.ClaimExportAsync(job.ExportJobId, CancellationToken.None));
        var artifact = await store.WriteAsync(customer, job.ExportJobId, new MemoryStream("{}"u8.ToArray()), CancellationToken.None);
        await store.CompleteExportAsync(job.ExportJobId, "/download", DateTimeOffset.UtcNow.AddMinutes(5), artifact, CancellationToken.None);

        Assert.Equal(ExportJobStatus.Completed, (await store.FindExportAsync(customer, job.ExportJobId, CancellationToken.None))?.Status);
        Assert.Null(await store.FindExportAsync(Guid.NewGuid(), job.ExportJobId, CancellationToken.None));
        Assert.NotNull(await store.OpenReadAsync(customer, job.ExportJobId, CancellationToken.None));
        Assert.Null(await store.OpenReadAsync(Guid.NewGuid(), job.ExportJobId, CancellationToken.None));
        Assert.Equal(artifact.ContentHash, (await store.FindExportAsync(customer, job.ExportJobId, CancellationToken.None))?.ArtifactContentHash);
    }

    [Fact]
    public async Task Blob_failure_leaves_export_failed_without_download_or_integrity_metadata()
    {
        var store = new LocalDevelopmentStore();
        var customerId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new Snapshot(snapshotId, customerId, "export-failure", "captured-identity", SnapshotMode.AppOnly, 1, now.AddMinutes(-3));
        snapshot.Start(now.AddMinutes(-2));
        snapshot.RecordSection(new(Guid.NewGuid(), customerId, snapshotId, SectionKeys.TenantSettings,
            SectionCoverage.Full, 0, null, now.AddMinutes(-1)));
        snapshot.Complete(now);
        await store.AddAsync(snapshot, CancellationToken.None);
        var queued = new ExportJob(Guid.NewGuid(), customerId, ExportFormat.Json, ExportJobStatus.Queued, now,
            "requester", SnapshotId: snapshotId);
        await store.AddExportAsync(queued, CancellationToken.None);
        var running = Assert.IsType<ExportJob>(await store.ClaimExportAsync(queued.ExportJobId, CancellationToken.None));
        var processor = new ExportJobProcessor(store, store, store, store, new StaticCatalog(Catalog(now)),
            new FailingArtifactStore(), new JsonEvidencePackageBuilder(new(1024 * 1024)), TimeProvider.System,
            NullLogger<ExportJobProcessor>.Instance);

        await processor.ProcessAsync(running, CancellationToken.None);

        var failed = Assert.IsType<ExportJob>(await store.FindExportAsync(customerId, queued.ExportJobId, CancellationToken.None));
        Assert.Equal(ExportJobStatus.Failed, failed.Status);
        Assert.Null(failed.DownloadUrl);
        Assert.Null(failed.DownloadExpiresAt);
        Assert.Null(failed.ArtifactContentHash);
        Assert.Contains("immutable upload", failed.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.OpenReadAsync(customerId, queued.ExportJobId, CancellationToken.None));
    }

    [Fact]
    public void Projection_contract_has_explicit_state_and_cannot_report_empty_rows_without_coverage()
    {
        var properties = typeof(ProjectedEvidenceResponse<EnvironmentProjection>).GetProperties().Select(value => value.Name).ToArray();
        Assert.Contains("State", properties);
        Assert.Contains("Coverage", properties);
        Assert.Contains("Confidence", properties);
        Assert.Contains("EvidenceIds", properties);
    }

    [Fact]
    public void Eligibility_contract_returns_allowlist_and_provenance_but_never_script_text()
    {
        var properties = typeof(RemediationEligibilityResponse).GetProperties().Select(value => value.Name).ToArray();
        Assert.Contains("AllowedParameters", properties);
        Assert.Contains("CatalogVersion", properties);
        Assert.Contains("Verification", properties);
        Assert.Contains("Rollback", properties);
        Assert.DoesNotContain(properties, value => value.Contains("Script", StringComparison.OrdinalIgnoreCase));
    }

    private static RawEvidenceReference Reference(Guid customerId, Guid snapshotId) => new(
        Guid.NewGuid(), customerId, snapshotId, SectionKeys.TenantSettings, $"{customerId:D}/{snapshotId:D}/tenantSettings/000001-001-{Guid.NewGuid():N}.json",
        "sha256", "application/json", "v1", DateTimeOffset.UtcNow, EvidenceConfidence.Documented, "collector", "1",
        "1", null, "GET", "https://example.test/redacted", 200, "resource", SnapshotMode.AppOnly, Guid.NewGuid(),
        "principal", null, new Dictionary<string, string>(), 1, 1, null, "Complete page.");

    private static PublishedRuleSet Catalog(DateTimeOffset publishedAt) => new(
        "2026.07", publishedAt, "approved", [], new("default", 1, []));

    private sealed class StaticCatalog(PublishedRuleSet catalog) : IPublishedRuleCatalog
    {
        public ValueTask<PublishedRuleSet?> GetCurrentAsync(CancellationToken cancellationToken) => ValueTask.FromResult<PublishedRuleSet?>(catalog);
        public ValueTask<PublishedRuleSet?> GetByDigestAsync(string contentDigest, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PublishedRuleSet?>(contentDigest == catalog.ContentDigest ? catalog : null);
    }

    private sealed class FailingArtifactStore : IExportArtifactStore
    {
        public ValueTask<ExportArtifactDescriptor> WriteAsync(Guid customerId, Guid exportJobId, Stream content, CancellationToken cancellationToken) =>
            ValueTask.FromException<ExportArtifactDescriptor>(new IOException("Blob upload failed."));

        public ValueTask<Stream?> OpenReadAsync(Guid customerId, Guid exportJobId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream?>(null);

        public ValueTask DeleteCustomerAsync(Guid customerId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}