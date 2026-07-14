using System.Collections.Concurrent;
using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Collectors;

public sealed record CapturedRawEvidence(RawEvidenceReference Reference, byte[] Content);

public sealed class LocalCollectorRuntimeStore : ISnapshotEvidenceSink, ICollectorCheckpointStore, ISectionProgressSink
{
    private readonly ConcurrentDictionary<Guid, CapturedRawEvidence> _evidence = new();
    private readonly ConcurrentDictionary<(Guid CustomerId, Guid SnapshotId, string SectionKey), CollectorCheckpoint> _checkpoints = new();
    private readonly ConcurrentQueue<SectionProgress> _progress = new();

    public IReadOnlyCollection<CapturedRawEvidence> Evidence => _evidence.Values.ToArray();
    public IReadOnlyCollection<SectionProgress> Progress => _progress.ToArray();

    public async ValueTask WriteRawAsync(RawEvidenceReference evidence, Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (!_evidence.TryAdd(evidence.RawEvidenceReferenceId, new(evidence, buffer.ToArray())))
        {
            throw new InvalidOperationException($"Raw evidence '{evidence.RawEvidenceReferenceId}' already exists.");
        }
    }

    public ValueTask<CollectorCheckpoint?> ReadAsync(Guid customerId, Guid snapshotId, string sectionKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _checkpoints.TryGetValue((customerId, snapshotId, sectionKey), out var checkpoint);
        return ValueTask.FromResult(checkpoint);
    }

    public ValueTask WriteAsync(CollectorCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _checkpoints[(checkpoint.CustomerId, checkpoint.SnapshotId, checkpoint.SectionKey)] = checkpoint;
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(Guid customerId, Guid snapshotId, string sectionKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _checkpoints.TryRemove((customerId, snapshotId, sectionKey), out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishAsync(SectionProgress progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _progress.Enqueue(progress);
        return ValueTask.CompletedTask;
    }
}