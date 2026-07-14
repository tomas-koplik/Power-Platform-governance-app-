using Azure.Core;
using Azure.Messaging.ServiceBus;
using Ppgsm.Core.Domain;

namespace Ppgsm.Collectors;

public sealed class SnapshotQueueOptions
{
    public const string SectionName = "Azure:ServiceBus";
    public string FullyQualifiedNamespace { get; init; } = string.Empty;
    public string QueueName { get; init; } = "snapshot-jobs";
}

public sealed record SnapshotCollectionJobEnvelope(Guid JobId);

public sealed class AzureServiceBusSnapshotJobPublisher : ISnapshotCollectionJobPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient? _client;
    private readonly ServiceBusSender _sender;

    public AzureServiceBusSnapshotJobPublisher(SnapshotQueueOptions options, TokenCredential credential)
    {
        if (string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace))
            throw new InvalidOperationException("Azure:ServiceBus:FullyQualifiedNamespace is required.");
        if (string.IsNullOrWhiteSpace(options.QueueName))
            throw new InvalidOperationException("Azure:ServiceBus:QueueName is required.");
        _client = new(options.FullyQualifiedNamespace, credential);
        _sender = _client.CreateSender(options.QueueName);
    }

    public AzureServiceBusSnapshotJobPublisher(ServiceBusSender sender) => _sender = sender;

    public async ValueTask PublishAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("Job ID is required.", nameof(jobId));
        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(new SnapshotCollectionJobEnvelope(jobId)))
        {
            MessageId = jobId.ToString("N"),
            CorrelationId = jobId.ToString("N"),
            ContentType = "application/json",
            Subject = "snapshot.collect"
        };
        await _sender.SendMessageAsync(message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
    }
}