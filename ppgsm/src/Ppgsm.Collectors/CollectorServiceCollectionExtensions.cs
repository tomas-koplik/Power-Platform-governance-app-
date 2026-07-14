using System.Security.Claims;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ppgsm.Collectors.Authentication;
using Ppgsm.Collectors.Clients;
using Ppgsm.Collectors.Collectors;
using Ppgsm.Collectors.Transport;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Collectors;

public static class CollectorServiceCollectionExtensions
{
    public static IServiceCollection AddPpgsmCollectors(this IServiceCollection services, IConfiguration configuration)
    {
        var transport = configuration.GetSection(CollectorTransportOptions.SectionName).Get<CollectorTransportOptions>() ?? new();
        var features = configuration.GetSection(CollectorFeatureOptions.SectionName).Get<CollectorFeatureOptions>() ?? new();
        var certificate = configuration.GetSection(AppOnlyCertificateOptions.SectionName).Get<AppOnlyCertificateOptions>() ?? new();

        services.AddSingleton(transport);
        services.AddSingleton(features);
        services.AddSingleton(certificate);
        services.AddSingleton<TokenCredential, DefaultAzureCredential>();
        services.AddSingleton<TenantConcurrencyLimiter>();
        services.AddSingleton<ICollectorDestinationResolver, CollectorDestinationResolver>();
        services.AddSingleton<CollectorDestinationPolicy>();
        services.AddSingleton<IAppOnlyTokenAcquirer, AzureIdentityCertificateTokenAcquirer>();
        services.AddTransient<ICollectorTokenProvider, CollectorTokenProvider>();
        services.AddHttpClient<ICollectorHttpPipeline, CollectorHttpPipeline>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PPGSM-Collectors/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        services.AddTransient<IBapApiClient, BapApiClient>();
        services.AddTransient<IPowerPlatformApiClient, PowerPlatformApiClient>();
        services.AddTransient<IPowerAppsApiClient, PowerAppsApiClient>();
        services.AddTransient<IFlowApiClient, FlowApiClient>();
        services.AddTransient<IGraphApiClient, GraphApiClient>();

        services.AddTransient<ISnapshotCollector, TenantSettingsCollector>();
        services.AddTransient<ISnapshotCollector, EnvironmentsCollector>();
        services.AddTransient<ISnapshotCollector, DlpPolicyCollector>();
        services.AddTransient<ISnapshotCollector, ConnectorsCollector>();
        services.AddTransient<ISnapshotCollector, TenantIsolationCollector>();
        services.AddTransient<ISnapshotCollector, AppsCollector>();
        services.AddTransient<ISnapshotCollector, FlowsCollector>();
        services.AddTransient<ISnapshotCollector, OwnerEnrichmentCollector>();
        services.AddTransient<ISnapshotCollector, EnvironmentGroupsCollector>();
        services.AddTransient<SnapshotCollectorOrchestrator>();
        return services;
    }
}

public sealed class UnavailableDelegatedTokenAcquirer : IDelegatedOboTokenAcquirer
{
    public ValueTask<string> GetAccessTokenAsync(Guid entraTenantId, string resourceScope, ClaimsPrincipal authenticatedPrincipal, CancellationToken cancellationToken) =>
        ValueTask.FromException<string>(new InvalidOperationException(
            "Delegated OBO is unavailable in this process. Run delegated collection in the authenticated API context."));
}