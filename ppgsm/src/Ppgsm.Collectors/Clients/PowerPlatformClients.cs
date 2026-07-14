using Ppgsm.Collectors.Authentication;
using Ppgsm.Collectors.Transport;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Collectors.Clients;

public interface IPowerPlatformApiClient
{
    Task<PagedCollectionResult> CollectAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken);
}

public interface IBapApiClient
{
    Task<PagedCollectionResult> CollectGetAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken);
    Task<PagedCollectionResult> CollectPostAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken);
}

public interface IPowerAppsApiClient
{
    Task<PagedCollectionResult> CollectAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken);
}

public interface IFlowApiClient
{
    Task<PagedCollectionResult> CollectAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken);
}

public interface IGraphApiClient
{
    Task<PagedCollectionResult> CollectAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken);
}

public sealed class PowerPlatformApiClient(ICollectorHttpPipeline pipeline) : IPowerPlatformApiClient
{
    public Task<PagedCollectionResult> CollectAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) =>
        pipeline.CollectPagesAsync(context, sectionKey, new(HttpMethod.Get, route, CollectorResources.PowerPlatform, apiVersion), sink, cancellationToken);
}

public sealed class BapApiClient(ICollectorHttpPipeline pipeline) : IBapApiClient
{
    public Task<PagedCollectionResult> CollectGetAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) =>
        pipeline.CollectPagesAsync(context, sectionKey, new(HttpMethod.Get, route, CollectorResources.BusinessApplications, apiVersion), sink, cancellationToken);

    public Task<PagedCollectionResult> CollectPostAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) =>
        pipeline.CollectPagesAsync(context, sectionKey, new(HttpMethod.Post, route, CollectorResources.BusinessApplications, apiVersion), sink, cancellationToken);
}

public sealed class PowerAppsApiClient(ICollectorHttpPipeline pipeline) : IPowerAppsApiClient
{
    public Task<PagedCollectionResult> CollectAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) =>
        pipeline.CollectPagesAsync(context, sectionKey, new(HttpMethod.Get, route, CollectorResources.BusinessApplications, apiVersion), sink, cancellationToken);
}

public sealed class FlowApiClient(ICollectorHttpPipeline pipeline) : IFlowApiClient
{
    public Task<PagedCollectionResult> CollectAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) =>
        pipeline.CollectPagesAsync(context, sectionKey, new(HttpMethod.Get, route, CollectorResources.Flow, apiVersion), sink, cancellationToken);
}

public sealed class GraphApiClient(ICollectorHttpPipeline pipeline) : IGraphApiClient
{
    public Task<PagedCollectionResult> CollectAsync(SnapshotCollectorContext context, string sectionKey, Uri route, string apiVersion, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) =>
        pipeline.CollectPagesAsync(context, sectionKey, new(HttpMethod.Get, route, CollectorResources.Graph, apiVersion), sink, cancellationToken);
}