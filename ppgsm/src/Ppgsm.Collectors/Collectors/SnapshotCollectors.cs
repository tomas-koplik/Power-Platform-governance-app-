using Ppgsm.Collectors.Clients;
using Ppgsm.Collectors.Transport;
using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Collectors.Collectors;

public sealed class TenantSettingsCollector(IBapApiClient client) : ISnapshotCollector
{
    private static readonly Uri Route = new("https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/listtenantsettings?api-version=2020-10-01");
    public string SectionKey => "tenantSettings";
    public CollectorRequirement Requirement => CollectorRequirement.DelegatedOnly;
    public CollectorConfidence Confidence => CollectorConfidence.Preview;

    public async Task<SectionResult> CollectAsync(SnapshotCollectorContext context, ISnapshotEvidenceSink sink, CancellationToken cancellationToken)
    {
        if (context.Mode != SnapshotMode.Delegated) return CollectorResults.Skipped("Documented app-only endpoint coverage remains unverified by PoC T-04.");
        var result = await client.CollectPostAsync(context, SectionKey, Route, "2020-10-01-preview", sink, cancellationToken);
        return CollectorResults.WithCompleteness(
            context, result, SectionKey, false,
            "Preview response shape retained as raw evidence; unknown properties are not discarded.");
    }
}

public sealed class EnvironmentsCollector(IBapApiClient client) : ISnapshotCollector
{
    private static readonly Uri Route = new("https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2020-10-01&$expand=properties");
    public string SectionKey => "environments";
    public CollectorRequirement Requirement => CollectorRequirement.DelegatedOnly;
    public CollectorConfidence Confidence => CollectorConfidence.Documented;

    public async Task<SectionResult> CollectAsync(SnapshotCollectorContext context, ISnapshotEvidenceSink sink, CancellationToken cancellationToken)
    {
        if (context.Mode != SnapshotMode.Delegated) return CollectorResults.Skipped("App-only BAP versus PPAPI coverage remains unverified by PoC T-04.");
        var result = await client.CollectGetAsync(context, SectionKey, Route, "2020-10-01", sink, cancellationToken);
        return CollectorResults.WithCompleteness(context, result, SectionKey, false);
    }
}

public sealed class CollectorFeatureOptions
{
    public const string SectionName = "Collectors:Features";
    public Dictionary<string, CollectorFeature> Sections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CollectorFeature
{
    public bool Enabled { get; set; }
    public Uri? Route { get; set; }
}

public abstract class FeatureGatedCollector(CollectorFeatureOptions options) : ISnapshotCollector
{
    public abstract string SectionKey { get; }
    public abstract CollectorRequirement Requirement { get; }
    public abstract CollectorConfidence Confidence { get; }
    protected abstract string ApiVersion { get; }
    protected abstract Task<PagedCollectionResult> CollectEnabledAsync(SnapshotCollectorContext context, Uri route, ISnapshotEvidenceSink sink, CancellationToken cancellationToken);

    public async Task<SectionResult> CollectAsync(SnapshotCollectorContext context, ISnapshotEvidenceSink sink, CancellationToken cancellationToken)
    {
        if (!options.Sections.TryGetValue(SectionKey, out var feature) || !feature.Enabled)
        {
            return CollectorResults.Skipped($"Collector is disabled by default; capability confidence is {Confidence} and requires source/PoC evidence.");
        }
        if (feature.Route is null || !feature.Route.IsAbsoluteUri)
        {
            return CollectorResults.Skipped("Collector was enabled without an explicitly verified absolute route.");
        }
        if (Requirement == CollectorRequirement.DelegatedOnly && context.Mode != SnapshotMode.Delegated)
        {
            return CollectorResults.Skipped("This endpoint identity is delegated-only until app-only coverage is proven.");
        }

        var result = await CollectEnabledAsync(context, feature.Route, sink, cancellationToken);
        var warning = $"Capability confidence is {Confidence}; successful transport does not establish complete tenant coverage.";
        return CollectorResults.WithCompleteness(
            context, result, SectionKey, RequiresEnvironmentScope, warning,
            "Unverified collector evidence cannot establish Full coverage before its PoC gate is passed.");
    }

    protected virtual bool RequiresEnvironmentScope => false;
    protected CollectorApiMetadata Metadata(Uri route) => new(route, ApiVersion);
}

public sealed record CollectorApiMetadata(Uri Route, string ApiVersion);

public sealed class DlpPolicyCollector(CollectorFeatureOptions options, IBapApiClient client) : FeatureGatedCollector(options)
{
    public override string SectionKey => SectionKeys.DlpPolicies;
    public override CollectorRequirement Requirement => CollectorRequirement.DelegatedOnly;
    public override CollectorConfidence Confidence => CollectorConfidence.PocRequired;
    protected override string ApiVersion => "unverified-T-02";
    protected override Task<PagedCollectionResult> CollectEnabledAsync(SnapshotCollectorContext context, Uri route, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) => client.CollectGetAsync(context, SectionKey, route, ApiVersion, sink, cancellationToken);
}

public sealed class ConnectorsCollector(CollectorFeatureOptions options, IPowerAppsApiClient client) : FeatureGatedCollector(options)
{
    public override string SectionKey => "connectors";
    public override CollectorRequirement Requirement => CollectorRequirement.DelegatedOnly;
    public override CollectorConfidence Confidence => CollectorConfidence.Documented;
    protected override string ApiVersion => "2020-10-01";
    protected override Task<PagedCollectionResult> CollectEnabledAsync(SnapshotCollectorContext context, Uri route, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) => client.CollectAsync(context, SectionKey, route, ApiVersion, sink, cancellationToken);
}

public sealed class TenantIsolationCollector(CollectorFeatureOptions options, IBapApiClient client) : FeatureGatedCollector(options)
{
    public override string SectionKey => "tenantIsolation";
    public override CollectorRequirement Requirement => CollectorRequirement.DelegatedOnly;
    public override CollectorConfidence Confidence => CollectorConfidence.PocRequired;
    protected override string ApiVersion => "unverified-T-03";
    protected override Task<PagedCollectionResult> CollectEnabledAsync(SnapshotCollectorContext context, Uri route, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) => client.CollectGetAsync(context, SectionKey, route, ApiVersion, sink, cancellationToken);
}

public sealed class AppsCollector(CollectorFeatureOptions options, IPowerAppsApiClient client) : FeatureGatedCollector(options)
{
    public override string SectionKey => "apps";
    public override CollectorRequirement Requirement => CollectorRequirement.DelegatedOnly;
    public override CollectorConfidence Confidence => CollectorConfidence.Documented;
    protected override string ApiVersion => "2020-10-01";
    protected override bool RequiresEnvironmentScope => true;
    protected override Task<PagedCollectionResult> CollectEnabledAsync(SnapshotCollectorContext context, Uri route, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) => client.CollectAsync(context, SectionKey, route, ApiVersion, sink, cancellationToken);
}

public sealed class FlowsCollector(CollectorFeatureOptions options, IFlowApiClient client) : FeatureGatedCollector(options)
{
    public override string SectionKey => "flows";
    public override CollectorRequirement Requirement => CollectorRequirement.DelegatedOnly;
    public override CollectorConfidence Confidence => CollectorConfidence.PocRequired;
    protected override string ApiVersion => "2016-11-01-unverified-T-05";
    protected override bool RequiresEnvironmentScope => true;
    protected override Task<PagedCollectionResult> CollectEnabledAsync(SnapshotCollectorContext context, Uri route, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) => client.CollectAsync(context, SectionKey, route, ApiVersion, sink, cancellationToken);
}

public sealed class OwnerEnrichmentCollector(CollectorFeatureOptions options, IGraphApiClient client) : FeatureGatedCollector(options)
{
    public override string SectionKey => SectionKeys.OwnerDirectory;
    public override CollectorRequirement Requirement => CollectorRequirement.DelegatedOnly;
    public override CollectorConfidence Confidence => CollectorConfidence.Documented;
    protected override string ApiVersion => "v1.0";
    protected override Task<PagedCollectionResult> CollectEnabledAsync(SnapshotCollectorContext context, Uri route, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) => client.CollectAsync(context, SectionKey, route, ApiVersion, sink, cancellationToken);
}

public sealed class EnvironmentGroupsCollector(CollectorFeatureOptions options, IPowerPlatformApiClient client) : FeatureGatedCollector(options)
{
    public override string SectionKey => "environmentGroups";
    public override CollectorRequirement Requirement => CollectorRequirement.DelegatedOnly;
    public override CollectorConfidence Confidence => CollectorConfidence.Preview;
    protected override string ApiVersion => "preview-unverified-T-04";
    protected override Task<PagedCollectionResult> CollectEnabledAsync(SnapshotCollectorContext context, Uri route, ISnapshotEvidenceSink sink, CancellationToken cancellationToken) => client.CollectAsync(context, SectionKey, route, ApiVersion, sink, cancellationToken);
}

internal static class CollectorResults
{
    public static SectionResult From(PagedCollectionResult result, params string[] warnings) => new(
        result.Coverage,
        result.ItemCount,
        result.Evidence,
        result.Warnings.Concat(warnings).ToArray());

    public static SectionResult Skipped(string reason) => new(SectionCoverage.Skipped, 0, [], [reason]);

    public static SectionResult WithCompleteness(
        SnapshotCollectorContext context,
        PagedCollectionResult result,
        string sectionKey,
        bool requiresEnvironmentScope,
        params string[] warnings)
    {
        var reasons = result.Warnings.Concat(warnings).ToList();
        if (!Enum.IsDefined(context.Tenant.Role))
            reasons.Add("Authenticated tenant role is unknown, so authorization scope completeness cannot be established.");
        if (context.RequestedSections is { Count: > 0 })
            reasons.Add("Snapshot collection used a selected section subset rather than the complete registered scope.");
        if (context.ExpectedEnvironmentIds is { Count: > 0 })
            reasons.Add("Snapshot collection used a selected environment subset rather than complete tenant scope.");
        if (requiresEnvironmentScope && context.ExpectedEnvironmentIds is not { Count: > 0 })
            reasons.Add($"Collector '{sectionKey}' has no verified expected environment scope.");
        if (context.ExpectedSubresources is { Count: > 0 } expected &&
            !expected.Contains(sectionKey, StringComparer.OrdinalIgnoreCase))
            reasons.Add($"Expected subresource scope does not include collector '{sectionKey}'.");

        var downgrade = result.Coverage == SectionCoverage.Full && reasons.Count > result.Warnings.Count;
        return new(downgrade ? SectionCoverage.Partial : result.Coverage, result.ItemCount, result.Evidence, reasons);
    }
}