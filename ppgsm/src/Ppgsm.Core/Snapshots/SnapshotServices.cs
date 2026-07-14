using System.Security.Claims;
using Ppgsm.Core.Domain;

namespace Ppgsm.Core.Snapshots;

public sealed record SnapshotRequest(
    string IdempotencyKey,
    SnapshotMode Mode,
    IReadOnlyCollection<string>? Sections = null,
    IReadOnlyCollection<string>? EnvironmentIds = null);

public interface ISnapshotStore
{
    ValueTask<Snapshot?> FindByIdAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken);
    ValueTask<Snapshot?> FindByIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<Snapshot>> ListAsync(Guid customerId, CancellationToken cancellationToken);
    ValueTask AddAsync(Snapshot snapshot, CancellationToken cancellationToken);
    ValueTask SaveAsync(Snapshot snapshot, CancellationToken cancellationToken);
}

public sealed class SnapshotRequestService(ISnapshotStore snapshots, TimeProvider timeProvider)
{
    public async ValueTask<(Snapshot Snapshot, bool Created)> RequestAsync(
        TenantContext tenant,
        SnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("Idempotency key is required.", nameof(request));

        var existing = await snapshots.FindByIdempotencyKeyAsync(tenant.CustomerId, request.IdempotencyKey.Trim(), cancellationToken);
        if (existing is not null) return (existing, false);

        var snapshot = new Snapshot(
            Guid.NewGuid(),
            tenant.CustomerId,
            request.IdempotencyKey,
            tenant.Subject.ToString(),
            request.Mode,
            schemaVersion: 1,
            timeProvider.GetUtcNow());
        await snapshots.AddAsync(snapshot, cancellationToken);
        return (snapshot, true);
    }
}

public enum CollectorRequirement { DelegatedOnly, AppOnlyCapable }
public enum CollectorConfidence { Documented, Preview, PocRequired }

public sealed record SnapshotCollectorContext(
    TenantContext Tenant,
    Guid SnapshotId,
    Guid EntraTenantId,
    SnapshotMode Mode,
    string CapturedIdentity,
    ClaimsPrincipal? AuthenticatedPrincipal,
    CollectorConfidence Confidence,
    IReadOnlyCollection<string>? RequestedSections = null,
    IReadOnlyCollection<string>? ExpectedEnvironmentIds = null,
    IReadOnlyCollection<string>? AuthoritativelyDiscoveredEnvironmentIds = null,
    IReadOnlyCollection<string>? ExpectedSubresources = null)
{
    public Guid CustomerId => Tenant.CustomerId;
}

public sealed record CollectorCheckpoint(
    Guid CustomerId,
    Guid SnapshotId,
    string SectionKey,
    string? ContinuationToken,
    int CompletedPages,
    int ItemCount,
    IReadOnlyCollection<Guid> EvidenceIds,
    IReadOnlyCollection<RawEvidenceReference> Evidence,
    DateTimeOffset UpdatedAt);

public interface ICollectorCheckpointStore
{
    ValueTask<CollectorCheckpoint?> ReadAsync(
        Guid customerId,
        Guid snapshotId,
        string sectionKey,
        CancellationToken cancellationToken);

    ValueTask WriteAsync(CollectorCheckpoint checkpoint, CancellationToken cancellationToken);
    ValueTask CompleteAsync(Guid customerId, Guid snapshotId, string sectionKey, CancellationToken cancellationToken);
}

public enum SectionProgressStatus { Started, PageCaptured, Completed }

public sealed record SectionProgress(
    Guid CustomerId,
    Guid SnapshotId,
    string SectionKey,
    SectionProgressStatus Status,
    int CompletedPages,
    int ItemCount,
    SectionCoverage? Coverage,
    string? Message,
    DateTimeOffset OccurredAt);

public interface ISectionProgressSink
{
    ValueTask PublishAsync(SectionProgress progress, CancellationToken cancellationToken);
}

public sealed record SectionResult(
    SectionCoverage Coverage,
    int ItemCount,
    IReadOnlyCollection<RawEvidenceReference> Evidence,
    IReadOnlyCollection<string> Warnings);

public interface ISnapshotEvidenceSink
{
    ValueTask WriteRawAsync(RawEvidenceReference evidence, Stream content, CancellationToken cancellationToken);
}

public interface ISnapshotCollector
{
    string SectionKey { get; }
    CollectorRequirement Requirement { get; }
    CollectorConfidence Confidence { get; }
    Task<SectionResult> CollectAsync(SnapshotCollectorContext context, ISnapshotEvidenceSink sink, CancellationToken cancellationToken);
}