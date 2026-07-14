namespace Ppgsm.Core.Domain;

public enum SnapshotStatus
{
    Queued,
    Running,
    Completed,
    Partial,
    Failed
}

public enum SectionCoverage
{
    Full,
    Partial,
    Failed,
    Skipped
}

public enum SnapshotMode
{
    Delegated,
    AppOnly
}

public sealed class Snapshot
{
    private readonly List<SnapshotSection> _sections = [];

    private Snapshot()
    {
    }

    public Snapshot(
        Guid snapshotId,
        Guid customerId,
        string idempotencyKey,
        string triggeredBy,
        SnapshotMode mode,
        int schemaVersion,
        DateTimeOffset requestedAt)
    {
        if (snapshotId == Guid.Empty) throw new ArgumentException("Snapshot ID is required.", nameof(snapshotId));
        if (customerId == Guid.Empty) throw new ArgumentException("Customer ID is required.", nameof(customerId));
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));
        if (string.IsNullOrWhiteSpace(triggeredBy)) throw new ArgumentException("Trigger identity is required.", nameof(triggeredBy));
        if (schemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));

        SnapshotId = snapshotId;
        CustomerId = customerId;
        IdempotencyKey = idempotencyKey.Trim();
        TriggeredBy = triggeredBy.Trim();
        Mode = mode;
        SchemaVersion = schemaVersion;
        RequestedAt = requestedAt;
        Status = SnapshotStatus.Queued;
    }

    public Guid SnapshotId { get; private set; }
    public Guid CustomerId { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string TriggeredBy { get; private set; } = string.Empty;
    public SnapshotMode Mode { get; private set; }
    public int SchemaVersion { get; private set; }
    public SnapshotStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? FailureReason { get; private set; }
    public IReadOnlyCollection<SnapshotSection> Sections => _sections.AsReadOnly();

    public void Start(DateTimeOffset startedAt)
    {
        EnsureStatus(SnapshotStatus.Queued);
        if (startedAt < RequestedAt) throw new DomainConflictException("A snapshot cannot start before it was requested.");

        Status = SnapshotStatus.Running;
        StartedAt = startedAt;
    }

    public void RecordSection(SnapshotSection section)
    {
        EnsureStatus(SnapshotStatus.Running);
        ArgumentNullException.ThrowIfNull(section);
        if (section.SnapshotId != SnapshotId) throw new DomainConflictException("Section belongs to a different snapshot.");
        if (_sections.Any(existing => string.Equals(existing.SectionKey, section.SectionKey, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainConflictException($"Section '{section.SectionKey}' has already been recorded.");
        }

        _sections.Add(section);
    }

    public void Complete(DateTimeOffset completedAt)
    {
        EnsureStatus(SnapshotStatus.Running);
        EnsureCompletionTime(completedAt);
        if (_sections.Count == 0) throw new DomainConflictException("A snapshot cannot complete without section coverage.");

        Status = _sections.All(section => section.Coverage == SectionCoverage.Full)
            ? SnapshotStatus.Completed
            : _sections.All(section => section.Coverage is SectionCoverage.Failed or SectionCoverage.Skipped)
                ? SnapshotStatus.Failed
                : SnapshotStatus.Partial;
        CompletedAt = completedAt;
    }

    public void Fail(string reason, DateTimeOffset completedAt)
    {
        if (Status is not (SnapshotStatus.Queued or SnapshotStatus.Running))
        {
            throw new DomainConflictException($"Snapshot in '{Status}' cannot fail.");
        }

        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Failure reason is required.", nameof(reason));
        EnsureCompletionTime(completedAt);
        Status = SnapshotStatus.Failed;
        FailureReason = reason.Trim();
        CompletedAt = completedAt;
    }

    private void EnsureStatus(SnapshotStatus expected)
    {
        if (Status != expected) throw new DomainConflictException($"Snapshot in '{Status}' cannot transition as if it were '{expected}'.");
    }

    private void EnsureCompletionTime(DateTimeOffset completedAt)
    {
        if (completedAt < (StartedAt ?? RequestedAt)) throw new DomainConflictException("Completion time precedes snapshot execution.");
    }
}

public sealed class SnapshotSection
{
    private SnapshotSection()
    {
    }

    public SnapshotSection(
        Guid snapshotSectionId,
        Guid customerId,
        Guid snapshotId,
        string sectionKey,
        SectionCoverage coverage,
        int itemCount,
        string? reason,
        DateTimeOffset recordedAt)
    {
        if (snapshotSectionId == Guid.Empty) throw new ArgumentException("Section ID is required.", nameof(snapshotSectionId));
        if (customerId == Guid.Empty) throw new ArgumentException("Customer ID is required.", nameof(customerId));
        if (snapshotId == Guid.Empty) throw new ArgumentException("Snapshot ID is required.", nameof(snapshotId));
        if (string.IsNullOrWhiteSpace(sectionKey)) throw new ArgumentException("Section key is required.", nameof(sectionKey));
        if (itemCount < 0) throw new ArgumentOutOfRangeException(nameof(itemCount));
        if (coverage is SectionCoverage.Failed or SectionCoverage.Skipped && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Failed and skipped sections require a reason.", nameof(reason));
        }

        SnapshotSectionId = snapshotSectionId;
        CustomerId = customerId;
        SnapshotId = snapshotId;
        SectionKey = sectionKey.Trim();
        Coverage = coverage;
        ItemCount = itemCount;
        Reason = reason?.Trim();
        RecordedAt = recordedAt;
    }

    public Guid SnapshotSectionId { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid SnapshotId { get; private set; }
    public string SectionKey { get; private set; } = string.Empty;
    public SectionCoverage Coverage { get; private set; }
    public int ItemCount { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
}

public sealed class DomainConflictException(string message) : InvalidOperationException(message);