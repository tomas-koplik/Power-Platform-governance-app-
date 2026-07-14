using Microsoft.Extensions.Configuration;

namespace Ppgsm.Collectors;

public sealed class CollectorRuntimeOptions
{
    public string AdapterMode { get; init; } = "Local";
    public string PersistenceMode { get; init; } = "Local";
    public string EvidenceStorageMode { get; init; } = "Local";
    public string QueueMode { get; init; } = "Local";

    public static CollectorRuntimeOptions Load(IConfiguration configuration) =>
        configuration.GetSection("Runtime").Get<CollectorRuntimeOptions>() ?? new();

    public void RequireProductionAdapters()
    {
        if (!string.Equals(AdapterMode, "SqlBlobServiceBus", StringComparison.Ordinal)
            || !string.Equals(PersistenceMode, "Sql", StringComparison.Ordinal)
            || !string.Equals(EvidenceStorageMode, "Blob", StringComparison.Ordinal)
            || !string.Equals(QueueMode, "ServiceBus", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Production requires Runtime AdapterMode=SqlBlobServiceBus, PersistenceMode=Sql, EvidenceStorageMode=Blob, and QueueMode=ServiceBus.");
        }
    }
}

public static class AzureCollectorOptions
{
    public static RawEvidenceBlobOptions Blob(IConfiguration configuration)
    {
        var endpoint = configuration["Azure:BlobEndpoint"];
        return new()
        {
            Endpoint = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri : null,
            ContainerName = configuration["Azure:RawSnapshotsContainerName"] ?? "raw-snapshots"
        };
    }

    public static SnapshotQueueOptions Queue(IConfiguration configuration) => new()
    {
        FullyQualifiedNamespace = configuration["Azure:ServiceBusFqdn"] ?? string.Empty,
        QueueName = configuration["Azure:SnapshotQueueName"] ?? "snapshot-jobs"
    };
}