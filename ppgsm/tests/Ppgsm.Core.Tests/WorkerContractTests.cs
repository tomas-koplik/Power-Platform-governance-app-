using Ppgsm.Worker;
using System.Text.Json;

namespace Ppgsm.Core.Tests;

public sealed class WorkerContractTests
{
    [Fact]
    public void Queue_message_contains_only_opaque_job_id()
    {
        var properties = typeof(SnapshotCollectionMessage).GetProperties();

        Assert.Single(properties);
        Assert.Equal("JobId", properties[0].Name);
        Assert.Equal(typeof(Guid), properties[0].PropertyType);
    }

    [Fact]
    public void Queue_message_cannot_serialize_delegated_identity_or_token()
    {
        var json = JsonSerializer.Serialize(new SnapshotCollectionMessage(Guid.NewGuid()));

        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("principal", json, StringComparison.OrdinalIgnoreCase);
        Assert.Single(JsonDocument.Parse(json).RootElement.EnumerateObject());
    }
}