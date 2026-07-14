using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Ppgsm.Collectors;
using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Worker;

public sealed record SnapshotCollectionMessage(Guid JobId);

public interface ISnapshotCollectionJobSource
{
    IAsyncEnumerable<SnapshotCollectionMessage> ReadAllAsync(CancellationToken cancellationToken);
    ValueTask CompleteAsync(SnapshotCollectionMessage message, CancellationToken cancellationToken);
    ValueTask AbandonAsync(SnapshotCollectionMessage message, Exception exception, CancellationToken cancellationToken);
}

public sealed class AzureServiceBusSnapshotCollectionJobSource : ISnapshotCollectionJobSource, IAsyncDisposable
{
    private sealed record Receipt(ServiceBusReceivedMessage Message);

    private readonly ServiceBusClient _client;
    private readonly ServiceBusReceiver _receiver;
    private readonly ConditionalWeakTable<SnapshotCollectionMessage, Receipt> _receipts = new();

    public AzureServiceBusSnapshotCollectionJobSource(SnapshotQueueOptions options, TokenCredential credential)
    {
        if (string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace))
            throw new InvalidOperationException("Azure:ServiceBus:FullyQualifiedNamespace is required.");
        if (string.IsNullOrWhiteSpace(options.QueueName))
            throw new InvalidOperationException("Azure:ServiceBus:QueueName is required.");
        _client = new(options.FullyQualifiedNamespace, credential);
        _receiver = _client.CreateReceiver(options.QueueName, new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });
    }

    public async IAsyncEnumerable<SnapshotCollectionMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var received = await _receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(20), cancellationToken);
            if (received is null) continue;
            SnapshotCollectionJobEnvelope? envelope;
            try
            {
                envelope = received.Body.ToObjectFromJson<SnapshotCollectionJobEnvelope>();
            }
            catch (JsonException exception)
            {
                await _receiver.DeadLetterMessageAsync(received, "InvalidEnvelope", Limit(exception.Message), cancellationToken);
                continue;
            }
            if (envelope is null || envelope.JobId == Guid.Empty)
            {
                await _receiver.DeadLetterMessageAsync(received, "InvalidJobId", "Message must contain one non-empty JobId.", cancellationToken);
                continue;
            }
            var message = new SnapshotCollectionMessage(envelope.JobId);
            _receipts.Add(message, new(received));
            yield return message;
        }
    }

    public async ValueTask CompleteAsync(SnapshotCollectionMessage message, CancellationToken cancellationToken)
    {
        var receipt = GetReceipt(message);
        await _receiver.CompleteMessageAsync(receipt.Message, cancellationToken);
        _receipts.Remove(message);
    }

    public async ValueTask AbandonAsync(SnapshotCollectionMessage message, Exception exception, CancellationToken cancellationToken)
    {
        var receipt = GetReceipt(message);
        if (exception is DomainConflictException or ArgumentException or JsonException)
        {
            await _receiver.DeadLetterMessageAsync(receipt.Message, exception.GetType().Name, Limit(exception.Message), cancellationToken);
        }
        else
        {
            await _receiver.AbandonMessageAsync(receipt.Message, new Dictionary<string, object>
            {
                ["lastFailureType"] = exception.GetType().Name,
                ["lastFailure"] = Limit(exception.Message)
            }, cancellationToken);
        }
        _receipts.Remove(message);
    }

    public async ValueTask DisposeAsync()
    {
        await _receiver.DisposeAsync();
        await _client.DisposeAsync();
    }

    private Receipt GetReceipt(SnapshotCollectionMessage message) =>
        _receipts.TryGetValue(message, out var receipt) ? receipt : throw new InvalidOperationException("Queue receipt is unavailable.");

    private static string Limit(string value) => value.Length <= 1024 ? value : value[..1024];
}

public sealed class SnapshotJobWorker(
    ISnapshotCollectionJobSource jobs,
    ISnapshotJobStore jobStore,
    ISnapshotStore snapshots,
    SnapshotCollectorOrchestrator orchestrator,
    ISnapshotEvidenceSink evidence,
    SnapshotEvaluationService evaluator,
    ILogger<SnapshotJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in jobs.ReadAllAsync(stoppingToken))
        {
            try
            {
                var job = await jobStore.LoadAsync(message.JobId, stoppingToken)
                    ?? throw new DomainConflictException("Queue message references an unknown snapshot job.");
                if (job.Status == SnapshotJobStatus.Completed)
                {
                    var completed = await snapshots.FindByIdAsync(job.CustomerId, job.SnapshotId, stoppingToken)
                        ?? throw new DomainConflictException("Completed snapshot job references a missing snapshot.");
                    await evaluator.EvaluateAndPersistAsync(job.CustomerId, job.SnapshotId, completed.SchemaVersion,
                        completed.Sections, job.RequestedEnvironmentIds, stoppingToken);
                    await jobs.CompleteAsync(message, stoppingToken);
                    continue;
                }
                if (job.Status is SnapshotJobStatus.Cancelled or SnapshotJobStatus.Failed)
                {
                    await jobs.CompleteAsync(message, stoppingToken);
                    continue;
                }
                if (job.Status != SnapshotJobStatus.Queued) throw new DomainConflictException($"Snapshot job is '{job.Status}', not queued.");
                var requestedSections = string.IsNullOrWhiteSpace(job.SectionsJson)
                    ? null
                    : System.Text.Json.JsonSerializer.Deserialize<string[]>(job.SectionsJson);
                var tenant = new TenantContext(job.CustomerId, job.Actor, MembershipRole.CustomerAdmin);
                var context = new SnapshotCollectorContext(tenant, job.SnapshotId, job.EntraTenantId, job.Mode, job.Actor.ToString(),
                    AuthenticatedPrincipal: null, CollectorConfidence.Documented, requestedSections, job.RequestedEnvironmentIds);
                await jobStore.MarkRunningAsync(job.JobId, stoppingToken);
                var sections = await orchestrator.ExecuteAsync(context, evidence, requestedSections, stoppingToken);
                await jobStore.CompleteAsync(job.JobId, sections, stoppingToken);
                await evaluator.EvaluateAndPersistAsync(job.CustomerId, job.SnapshotId, 1, sections, job.RequestedEnvironmentIds, stoppingToken);
                await jobs.CompleteAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Snapshot collection failed for durable job {JobId}.", message.JobId);
                try
                {
                    var failedJob = await jobStore.LoadAsync(message.JobId, stoppingToken);
                    if (failedJob?.Status is SnapshotJobStatus.Queued or SnapshotJobStatus.Running)
                        await jobStore.FailAsync(message.JobId, exception.Message, stoppingToken);
                }
                catch (DomainConflictException)
                {
                    logger.LogWarning("Cannot persist failure state for durable job {JobId}.", message.JobId);
                }
                await jobs.AbandonAsync(message, exception, stoppingToken);
            }
        }
    }
}