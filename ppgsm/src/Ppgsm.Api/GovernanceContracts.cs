using Ppgsm.Core.Domain;

namespace Ppgsm.Api;

public enum ApiCapability
{
    Portfolio, Onboarding, Connections, Snapshots, Evidence, Findings, Score, Dlp,
    Compare, Exports, Exceptions, Remediation, Approvals, DirectExecution, CloudQueue, CloudStorage
}

public sealed class ApiCapabilityRegistry(IPublishedRuleCatalog rules, ITrustedRemediationTemplateCatalog templates)
{
    private static readonly HashSet<ApiCapability> Enabled =
    [
        ApiCapability.Portfolio, ApiCapability.Onboarding, ApiCapability.Connections, ApiCapability.Snapshots, ApiCapability.Evidence,
        ApiCapability.Findings, ApiCapability.Dlp, ApiCapability.Compare, ApiCapability.Exports,
        ApiCapability.Exceptions, ApiCapability.Remediation, ApiCapability.Approvals
    ];

    public IReadOnlyDictionary<string, bool> Status => Enum.GetValues<ApiCapability>()
        .ToDictionary(value => value.ToString(), IsEnabled, StringComparer.OrdinalIgnoreCase);

    public void Require(ApiCapability capability)
    {
        if (!IsEnabled(capability)) throw new CapabilityUnavailableException(capability);
    }

    private bool IsEnabled(ApiCapability capability)
    {
        var published = capability is ApiCapability.Score or ApiCapability.Remediation
            ? rules.GetCurrentAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult()
            : null;
        return capability switch
        {
            ApiCapability.Score => published is not null,
            ApiCapability.Remediation => published?.Rules.Any(rule => rule.Remediation.Type == RemediationKind.Script &&
                !string.IsNullOrWhiteSpace(rule.Remediation.TemplateId) && templates.Find(rule.Remediation.TemplateId) is not null) == true,
            _ => Enabled.Contains(capability)
        };
    }
}

public sealed class NoPublishedRuleCatalog : IPublishedRuleCatalog
{
    public ValueTask<PublishedRuleSet?> GetCurrentAsync(CancellationToken cancellationToken) => ValueTask.FromResult<PublishedRuleSet?>(null);
}

public sealed class CapabilityUnavailableException(ApiCapability capability)
    : InvalidOperationException($"Capability '{capability}' is not available in this deployment.");

public sealed record WorkspaceSessionResponse(string DisplayName, MembershipRole Role, string AuthMode);
public sealed record WorkspaceConnectionResponse(ConnectionMode Mode, ConnectionStatus Status, string Detail);
public sealed record WorkspaceCustomerResponse(Guid CustomerId, string Name, Guid EntraTenantId, string Region, CustomerStatus Status, WorkspaceConnectionResponse Connection);
public sealed record WorkspaceScoreResponse(int Overall, string Tier, int Evaluated, int Total, SectionCoverage Confidence, IReadOnlyDictionary<string, int> Areas);
public sealed record WorkspaceResponse(
    WorkspaceSessionResponse Session,
    IReadOnlyList<WorkspaceCustomerResponse> Customers,
    IReadOnlyList<SnapshotResponse> Snapshots,
    WorkspaceScoreResponse Score,
    IReadOnlyList<object> Settings,
    IReadOnlyList<object> Environments,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<object> DlpPolicies,
    IReadOnlyDictionary<string, bool> Capabilities);

public sealed record CreateExceptionRequest(string Reason, DateTimeOffset ExpiresAt);
public sealed record CreateExportRequest(ExportFormat Format, Guid SnapshotId, bool IncludePii = false);
public sealed record CreateRemediationProposalRequest(Guid FindingId, Guid SnapshotId, string TemplateId, JsonElement Parameters, string EvidenceHash, string TargetScope, DateTimeOffset EvidenceCapturedAt, DateTimeOffset EvidenceValidUntil);
public sealed record ReviewRemediationRequest(bool Approved, string? Reason, Guid LatestSnapshotId);
public sealed record RequestOffboardingRequest(DateTimeOffset RetentionExpiresAt);

public sealed record EvidenceMetadataResponse(Guid EvidenceId, string Section, string MediaType, string ContentHash,
    DateTimeOffset CapturedAt, EvidenceConfidence Confidence, int PageNumber, string CollectorId, string CollectorVersion,
    string ParserSchemaVersion, string CompletenessRationale);
public sealed record EvidenceIndexResponse(Guid SnapshotId, SectionCoverage Coverage, EvidenceConfidence Confidence,
    IReadOnlyCollection<Guid> EvidenceIds, IReadOnlyCollection<EvidenceMetadataResponse> Items, int Page, int PageSize, int Total);
public sealed record ProjectedEvidenceResponse<T>(Guid SnapshotId, string State, SectionCoverage Coverage,
    EvidenceConfidence Confidence, IReadOnlyCollection<Guid> EvidenceIds, IReadOnlyCollection<T> Items, string Detail);
public sealed record TenantSettingProjection(string Key, bool? Value, Guid EvidenceId);
public sealed record EnvironmentProjection(string Id, string DisplayName, string Type, string Region, bool IsDefault,
    bool IsManaged, string? ProtectionLevel, bool HasDataverse, Guid? SecurityGroupId, Guid EvidenceId);
public sealed record DlpPolicyProjection(string Id, string DisplayName, JsonElement Properties, Guid EvidenceId);

public sealed record ConsentDocumentResponse(Guid CustomerId, Guid EntraTenantId, ConnectionStatus Status,
    IReadOnlyCollection<string> ExactScopes, IReadOnlyCollection<string> DataCategories, string Retention,
    string Region, string Revocation, IReadOnlyCollection<TenantCapability> CapabilityEvidence, DateTimeOffset? LastValidatedAt);

public sealed record RemediationEligibilityResponse(bool Eligible, string? Reason, Guid FindingId, Guid SnapshotId,
    string? RuleId, int? RuleVersion, string? CatalogVersion, string? TemplateId, int? TemplateVersion,
    IReadOnlyCollection<string> AllowedParameters, DateTimeOffset EvidenceCapturedAt, DateTimeOffset EvidenceValidUntil,
    string Target, string Verification, string Rollback);

public sealed record DeletionCertificateResponse(string CertificateId, Guid CustomerId, Guid JobId, DateTimeOffset RequestedAt,
    DateTimeOffset? ApprovedAt, DateTimeOffset? StartedAt, DateTimeOffset CompletedAt, DateTimeOffset RetentionExpiresAt,
    string Outcome, IReadOnlyDictionary<string, long> BeforeCounts, IReadOnlyDictionary<string, long> AfterCounts,
    string ConsentRevocationReference, string PhysicalDeletionReference);