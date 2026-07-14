using System.Text.Json;

namespace Ppgsm.Core.Domain;

public sealed record RawEvidenceReference(
    Guid RawEvidenceReferenceId,
    Guid CustomerId,
    Guid SnapshotId,
    string SectionKey,
    string StoragePath,
    string ContentHash,
    string MediaType,
    string ApiVersion,
    DateTimeOffset CapturedAt,
    EvidenceConfidence LifecycleConfidence,
    string CollectorId,
    string CollectorVersion,
    string ParserSchemaVersion,
    string? PocReference,
    string Method,
    string SanitizedUri,
    int StatusCode,
    string TokenResource,
    SnapshotMode AuthMode,
    Guid TenantIdentityBasis,
    string PrincipalIdentityBasis,
    string? RequestId,
    IReadOnlyDictionary<string, string> RedactedHeaders,
    int PageNumber,
    int AttemptNumber,
    Guid? PreviousEvidenceId,
    string CompletenessRationale);

public enum EvidenceConfidence { Documented, Preview, PocRequired }

public sealed record CollectorCheckpointRecord(
    Guid CustomerId,
    Guid SnapshotId,
    string SectionKey,
    string? ContinuationToken,
    int CompletedPages,
    int ItemCount,
    string EvidenceIdsJson,
    DateTimeOffset UpdatedAt);

public sealed record CollectorProgressRecord(
    long CollectorProgressRecordId,
    Guid CustomerId,
    Guid SnapshotId,
    string SectionKey,
    string Status,
    int CompletedPages,
    int ItemCount,
    string? Coverage,
    string? Message,
    DateTimeOffset OccurredAt);

public sealed record TenantSettingsEvidence(
    Guid TenantSettingsEvidenceId,
    Guid CustomerId,
    Guid SnapshotId,
    bool? TrialEnvironmentsDisabled,
    bool? DeveloperEnvironmentsRestricted,
    bool? CopilotDataMovementRestricted,
    JsonDocument KnownSettings,
    Guid RawEvidenceReferenceId);

public sealed record EnvironmentEvidence(
    Guid EnvironmentEvidenceId,
    Guid CustomerId,
    Guid SnapshotId,
    string EnvironmentId,
    string DisplayName,
    string EnvironmentType,
    string Region,
    bool IsDefault,
    bool IsManaged,
    string? ProtectionLevel,
    bool HasDataverse,
    Guid? SecurityGroupId,
    JsonDocument Properties,
    Guid RawEvidenceReferenceId);

public sealed record DlpPolicyEvidence(
    Guid DlpPolicyEvidenceId,
    Guid CustomerId,
    Guid SnapshotId,
    string PolicyId,
    string DisplayName,
    JsonDocument Properties,
    Guid RawEvidenceReferenceId);

public sealed record EvidenceMetadataPage(
    IReadOnlyCollection<RawEvidenceReference> Items,
    int Page,
    int PageSize,
    int Total);

public sealed record NormalizedEvidenceProjection(
    IReadOnlyCollection<TenantSettingsEvidence> TenantSettings,
    IReadOnlyCollection<EnvironmentEvidence> Environments,
    IReadOnlyCollection<DlpPolicyEvidence> DlpPolicies);

public sealed record SnapshotEnvironmentScope(
    Guid SnapshotId,
    Guid CustomerId,
    string RequestedEnvironmentIdsJson,
    string DiscoveredEnvironmentIdsJson,
    bool DiscoveryAuthoritative,
    DateTimeOffset RecordedAt);

public sealed record AuditEvent(
    long AuditId,
    Guid CustomerId,
    Guid ActorTenantId,
    Guid ActorObjectId,
    string Action,
    string? TargetType,
    string? TargetId,
    DateTimeOffset Timestamp,
    string Outcome,
    int StatusCode,
    string? IpAddress,
    JsonDocument? Details,
    string CorrelationId)
{
    public SubjectIdentity Actor => SubjectIdentity.Create(ActorTenantId, ActorObjectId);
}

public interface IAuditSink
{
    ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}