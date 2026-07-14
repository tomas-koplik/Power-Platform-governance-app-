using System.Text.Json;
using System.Security.Cryptography;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Moq;
using Ppgsm.Collectors;
using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Collectors.Tests;

public sealed class ProductionRuntimeAdapterTests
{
    [Fact]
    public void Evidence_path_is_derived_from_identity_and_page()
    {
        var customerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var snapshotId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var evidenceId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        var path = RawEvidenceBlobPath.Derive(customerId, snapshotId, "environments", 12, 2, evidenceId, "json");

        Assert.Equal("11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/environments/000012-002-33333333333333333333333333333333.json", path);
        Assert.True(RawEvidenceBlobPath.IsCanonical(path));
        Assert.True(RawEvidenceBlobPath.BelongsToCustomer(path, customerId));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("tenant/settings")]
    [InlineData("")]
    public void Evidence_path_rejects_caller_controlled_section_segments(string section)
    {
        Assert.Throws<ArgumentException>(() => RawEvidenceBlobPath.Derive(
            Guid.NewGuid(), Guid.NewGuid(), section, 1, 1, Guid.NewGuid(), "json"));
    }

    [Fact]
    public void Evidence_path_rejects_cross_customer_substitution()
    {
        var owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var attacker = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var path = RawEvidenceBlobPath.Derive(owner, Guid.NewGuid(), "apps", 1, 1, Guid.NewGuid(), "json");

        Assert.False(RawEvidenceBlobPath.BelongsToCustomer(path, attacker));
    }

    [Fact]
    public void Queue_envelope_serializes_only_opaque_job_id()
    {
        var jobId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new SnapshotCollectionJobEnvelope(jobId)));

        var property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("JobId", property.Name);
        Assert.Equal(jobId, property.Value.GetGuid());
        Assert.DoesNotContain("token", document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_runtime_rejects_any_placeholder_mode()
    {
        var options = new CollectorRuntimeOptions
        {
            AdapterMode = "SqlBlobServiceBus",
            PersistenceMode = "Sql",
            EvidenceStorageMode = "Local",
            QueueMode = "ServiceBus"
        };

        Assert.Throws<InvalidOperationException>(options.RequireProductionAdapters);
    }

    [Fact]
    public void Azure_options_contain_endpoints_and_names_but_no_credentials()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Azure:BlobEndpoint"] = "https://example.blob.core.windows.net/",
            ["Azure:ServiceBusFqdn"] = "example.servicebus.windows.net"
        }).Build();

        var blob = AzureCollectorOptions.Blob(configuration);
        var queue = AzureCollectorOptions.Queue(configuration);

        Assert.Equal("https://example.blob.core.windows.net/", blob.Endpoint?.AbsoluteUri);
        Assert.Equal("example.servicebus.windows.net", queue.FullyQualifiedNamespace);
        Assert.DoesNotContain(blob.GetType().GetProperties(), property => property.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queue.GetType().GetProperties(), property => property.Name.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Service_bus_publisher_sets_duplicate_id_and_sends_only_job_id()
    {
        ServiceBusMessage? sent = null;
        var sender = new Mock<ServiceBusSender>();
        sender.Setup(value => value.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((message, _) => sent = message)
            .Returns(Task.CompletedTask);
        await using var publisher = new AzureServiceBusSnapshotJobPublisher(sender.Object);
        var jobId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        await publisher.PublishAsync(jobId, TestContext.Current.CancellationToken);

        Assert.NotNull(sent);
        Assert.Equal(jobId.ToString("N"), sent.MessageId);
        using var body = JsonDocument.Parse(sent.Body);
        var property = Assert.Single(body.RootElement.EnumerateObject());
        Assert.Equal("JobId", property.Name);
        Assert.Equal(jobId, property.Value.GetGuid());
    }

    [Fact]
    public async Task Azurite_round_trip_preserves_content_and_rejects_cross_tenant_delete()
    {
        var connectionString = Environment.GetEnvironmentVariable("AZURITE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var container = new BlobContainerClient(connectionString, $"raw-{Guid.NewGuid():N}");
        await container.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            var store = new AzureBlobRawEvidenceContentStore(container);
            var content = "{\"value\":[1]}"u8.ToArray();
            var evidence = Evidence(content);

            await store.WriteAsync(evidence, new MemoryStream(content), TestContext.Current.CancellationToken);
            await using var read = await store.OpenReadAsync(evidence.StoragePath, TestContext.Current.CancellationToken);
            Assert.NotNull(read);
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy, TestContext.Current.CancellationToken);
            Assert.Equal(content, copy.ToArray());
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.DeleteCustomerAsync(Guid.NewGuid(), [evidence.StoragePath], TestContext.Current.CancellationToken));
            await store.DeleteCustomerAsync(evidence.CustomerId, [evidence.StoragePath], TestContext.Current.CancellationToken);
            Assert.Null(await store.OpenReadAsync(evidence.StoragePath, TestContext.Current.CancellationToken));
        }
        finally
        {
            await container.DeleteIfExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private static RawEvidenceReference Evidence(byte[] content)
    {
        var customerId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        return new(evidenceId, customerId, snapshotId, "apps",
            RawEvidenceBlobPath.Derive(customerId, snapshotId, "apps", 1, 1, evidenceId, "json"),
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), "application/json", "v1", DateTimeOffset.UtcNow,
            EvidenceConfidence.Documented, "apps", "1.0", "raw-json-v1", null, "GET", "https://example.test/apps", 200,
            "https://example.test/.default", SnapshotMode.AppOnly, Guid.NewGuid(), "service", null,
            new Dictionary<string, string>(), 1, 1, null, "Complete response.");
    }
}