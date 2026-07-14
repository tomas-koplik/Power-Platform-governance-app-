using Ppgsm.Core.Snapshots;

namespace Ppgsm.Core.Domain;

public interface ICustomerStore
{
    ValueTask<Customer?> FindCustomerAsync(Guid customerId, CancellationToken cancellationToken);
    ValueTask<Customer> CreateCustomerAsync(string name, Guid entraTenantId, string region, SubjectIdentity creator, CancellationToken cancellationToken);
}

public interface IGovernanceStore
{
    ValueTask<IReadOnlyList<Finding>> ListFindingsAsync(Guid customerId, Guid snapshotId, DateTimeOffset now, CancellationToken cancellationToken);
    ValueTask ReplaceFindingsAsync(Guid customerId, Guid snapshotId, IReadOnlyCollection<Finding> findings, CancellationToken cancellationToken);
    ValueTask<Finding?> FindFindingAsync(Guid customerId, Guid snapshotId, Guid findingId, CancellationToken cancellationToken);
    ValueTask<RawEvidenceReference?> FindEvidenceByHashAsync(Guid customerId, Guid snapshotId, string evidenceHash, CancellationToken cancellationToken);
    ValueTask<GovernanceException> AddExceptionAsync(GovernanceException exception, CancellationToken cancellationToken);
    ValueTask<ExportJob> AddExportAsync(ExportJob job, CancellationToken cancellationToken);
    ValueTask<ExportJob?> FindExportAsync(Guid customerId, Guid exportJobId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyCollection<Guid>> ListQueuedExportsAsync(CancellationToken cancellationToken);
    ValueTask<ExportJob?> ClaimExportAsync(Guid exportJobId, CancellationToken cancellationToken);
    ValueTask CompleteExportAsync(Guid exportJobId, string artifactPath, DateTimeOffset downloadExpiresAt,
        ExportArtifactDescriptor artifact, CancellationToken cancellationToken);
    ValueTask FailExportAsync(Guid exportJobId, string reason, CancellationToken cancellationToken);
    ValueTask<RemediationProposal> AddProposalAsync(RemediationProposal proposal, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<RemediationProposal>> ListProposalsAsync(Guid customerId, RemediationProposalStatus? status, CancellationToken cancellationToken);
    ValueTask<RemediationProposal?> FindProposalAsync(Guid customerId, Guid proposalId, CancellationToken cancellationToken);
    ValueTask SaveProposalAsync(RemediationProposal proposal, CancellationToken cancellationToken);
}

public interface IExportDownloadAuthorizer
{
    ValueTask<Uri?> CreateAuthorizedDownloadAsync(ExportJob job, TimeSpan lifetime, CancellationToken cancellationToken);
}

public sealed record EvaluationEvidencePayload(RawEvidenceReference Reference, byte[] Content);

public interface IEvaluationEvidenceStore
{
    ValueTask<IReadOnlyCollection<EvaluationEvidencePayload>> LoadEvaluationEvidenceAsync(
        Guid customerId, Guid snapshotId, CancellationToken cancellationToken);
    ValueTask SaveEnvironmentScopeAsync(SnapshotEnvironmentScope scope, CancellationToken cancellationToken);
    ValueTask SaveNormalizedEvidenceAsync(Guid customerId, Guid snapshotId, NormalizedEvidenceProjection projection, CancellationToken cancellationToken);
}

public interface IPocApprovalStore
{
    ValueTask<PocApproval> AddPocApprovalAsync(PocApproval approval, CancellationToken cancellationToken);
    ValueTask<IReadOnlySet<string>> GetApprovedRuleIdsAsync(
        Guid customerId, string identity, string apiVersion, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IEvidenceProjectionStore
{
    ValueTask<EvidenceMetadataPage> ListEvidenceMetadataAsync(Guid customerId, Guid snapshotId, int page, int pageSize, CancellationToken cancellationToken);
    ValueTask<IReadOnlyCollection<TenantSettingsEvidence>> ListTenantSettingsAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyCollection<EnvironmentEvidence>> ListEnvironmentsAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyCollection<DlpPolicyEvidence>> ListDlpPoliciesAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken);
}

public interface IExportArtifactStore
{
    ValueTask<ExportArtifactDescriptor> WriteAsync(Guid customerId, Guid exportJobId, Stream content, CancellationToken cancellationToken);
    ValueTask<Stream?> OpenReadAsync(Guid customerId, Guid exportJobId, CancellationToken cancellationToken);
    ValueTask DeleteCustomerAsync(Guid customerId, CancellationToken cancellationToken);
}

public sealed record ExportArtifactDescriptor(string ContentHash, long ContentLength, string MediaType, string StorageETag);

public sealed record AuthorizedRawEvidence(RawEvidenceReference Reference, Stream Content);

public interface IRawEvidenceContentStore
{
    ValueTask WriteAsync(RawEvidenceReference evidence, Stream content, CancellationToken cancellationToken);
    ValueTask<Stream?> OpenReadAsync(string storagePath, CancellationToken cancellationToken);
    ValueTask DeleteCustomerAsync(Guid customerId, IReadOnlyCollection<string> storagePaths, CancellationToken cancellationToken);
}

public interface IRawEvidenceAuthorizationStore
{
    ValueTask<AuthorizedRawEvidence?> OpenAuthorizedAsync(Guid customerId, Guid snapshotId, Guid evidenceId, CancellationToken cancellationToken);
}

public enum SnapshotJobStatus { Queued, Running, Completed, Failed, Cancelled }

public sealed record SnapshotJobRecord(
    Guid JobId,
    Guid CustomerId,
    Guid SnapshotId,
    Guid ConnectionId,
    Guid EntraTenantId,
    SnapshotMode Mode,
    Guid ActorTenantId,
    Guid ActorObjectId,
    string? SectionsJson,
    string? EnvironmentIdsJson,
    SnapshotJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? FailureReason)
{
    public SubjectIdentity Actor => SubjectIdentity.Create(ActorTenantId, ActorObjectId);
    public IReadOnlyCollection<string>? RequestedEnvironmentIds => string.IsNullOrWhiteSpace(EnvironmentIdsJson)
        ? null
        : System.Text.Json.JsonSerializer.Deserialize<string[]>(EnvironmentIdsJson);
}

public interface ISnapshotJobStore
{
    ValueTask AddAsync(SnapshotJobRecord job, CancellationToken cancellationToken);
    ValueTask<SnapshotJobRecord?> LoadAsync(Guid jobId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyCollection<Guid>> ListQueuedAsync(CancellationToken cancellationToken);
    ValueTask MarkRunningAsync(Guid jobId, CancellationToken cancellationToken);
    ValueTask CompleteAsync(Guid jobId, IReadOnlyCollection<SnapshotSection> sections, CancellationToken cancellationToken);
    ValueTask FailAsync(Guid jobId, string reason, CancellationToken cancellationToken);
}

public interface ISnapshotCollectionJobPublisher
{
    ValueTask PublishAsync(Guid jobId, CancellationToken cancellationToken);
}

public sealed record CustomerLegalHold(Guid CustomerId, string Reason, DateTimeOffset PlacedAt, DateTimeOffset? ReleasedAt)
{
    public bool IsActive => ReleasedAt is null;
}

public enum DeletionStatus { Requested, Approved, Executing, PendingRetentionExpiry, Completed, Failed }

public sealed class CustomerDeletionRecord
{
    public CustomerDeletionRecord(Guid jobId, Guid customerId, string requestedBy, DateTimeOffset requestedAt, DateTimeOffset retentionExpiresAt)
    {
        if (jobId == Guid.Empty || customerId == Guid.Empty) throw new ArgumentException("Offboarding job and customer IDs are required.");
        if (string.IsNullOrWhiteSpace(requestedBy)) throw new ArgumentException("The requester is required.", nameof(requestedBy));
        JobId = jobId;
        CustomerId = customerId;
        RequestedBy = requestedBy;
        RequestedAt = requestedAt;
        RetentionExpiresAt = retentionExpiresAt;
        Status = DeletionStatus.Requested;
    }

    public Guid CustomerId { get; }
    public Guid JobId { get; }
    public DeletionStatus Status { get; private set; }
    public string RequestedBy { get; }
    public DateTimeOffset RequestedAt { get; }
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset RetentionExpiresAt { get; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? Detail { get; private set; }
    public string? CertificateId { get; private set; }
    public string? BeforeCountsJson { get; private set; }
    public string? AfterCountsJson { get; private set; }
    public string? ConsentRevocationReference { get; private set; }
    public string? PhysicalDeletionReference { get; private set; }

    public void Approve(string approver, DateTimeOffset now)
    {
        if (Status != DeletionStatus.Requested) throw new DomainConflictException("Only requested offboarding can be approved.");
        if (string.Equals(RequestedBy, approver, StringComparison.OrdinalIgnoreCase)) throw new DomainConflictException("The requester cannot approve their own offboarding request.");
        if (string.IsNullOrWhiteSpace(approver)) throw new ArgumentException("The approver is required.", nameof(approver));
        ApprovedBy = approver;
        ApprovedAt = now;
        Status = DeletionStatus.Approved;
    }

    public void WaitForRetention(DateTimeOffset now)
    {
        if (Status != DeletionStatus.Approved || now >= RetentionExpiresAt) throw new DomainConflictException("Offboarding is not waiting for retention expiry.");
        Status = DeletionStatus.PendingRetentionExpiry;
    }

    public void MarkRetentionReady(DateTimeOffset now)
    {
        if (Status != DeletionStatus.PendingRetentionExpiry || now < RetentionExpiresAt) throw new DomainConflictException("The retention period has not expired.");
        Status = DeletionStatus.Approved;
    }

    public void Start(DateTimeOffset now, IReadOnlyDictionary<string, long> beforeCounts)
    {
        if (Status != DeletionStatus.Approved) throw new DomainConflictException("Only approved offboarding can execute.");
        Status = DeletionStatus.Executing;
        StartedAt = now;
        BeforeCountsJson = JsonSerializer.Serialize(beforeCounts);
    }

    public void Complete(DateTimeOffset now, IReadOnlyDictionary<string, long> afterCounts, string consentReference, string deletionReference)
    {
        if (Status != DeletionStatus.Executing) throw new DomainConflictException("Offboarding is not executing.");
        if (afterCounts.Values.Any(value => value != 0)) throw new DomainConflictException("Physical deletion verification found retained tenant rows or evidence.");
        if (string.IsNullOrWhiteSpace(consentReference) || string.IsNullOrWhiteSpace(deletionReference))
            throw new DomainConflictException("External revocation and physical deletion evidence are required.");
        Status = DeletionStatus.Completed;
        CompletedAt = now;
        AfterCountsJson = JsonSerializer.Serialize(afterCounts);
        ConsentRevocationReference = consentReference;
        PhysicalDeletionReference = deletionReference;
        CertificateId = Guid.NewGuid().ToString("N");
        Detail = "Deletion verified by external adapter results and zero after-counts.";
    }

    public void Fail(string detail)
    {
        if (Status == DeletionStatus.Completed) throw new DomainConflictException("Completed offboarding cannot fail.");
        Status = DeletionStatus.Failed;
        Detail = string.IsNullOrWhiteSpace(detail) ? "Offboarding failed without adapter evidence." : detail;
    }
}

public interface ICustomerOffboardingStore
{
    ValueTask<CustomerLegalHold?> GetLegalHoldAsync(Guid customerId, CancellationToken cancellationToken);
    ValueTask<CustomerDeletionRecord?> GetDeletionAsync(Guid customerId, CancellationToken cancellationToken);
    ValueTask<CustomerDeletionRecord?> GetDeletionJobAsync(Guid jobId, CancellationToken cancellationToken);
    ValueTask SaveDeletionAsync(CustomerDeletionRecord deletion, CancellationToken cancellationToken);
    ValueTask<IReadOnlyCollection<Guid>> ListExecutableDeletionJobsAsync(DateTimeOffset now, CancellationToken cancellationToken);
    ValueTask<IReadOnlyDictionary<string, long>> CountTenantDataAsync(Guid customerId, CancellationToken cancellationToken);
    ValueTask<PhysicalDeletionResult> DeleteTenantDataAsync(Guid customerId, CancellationToken cancellationToken);
}

public interface ICustomerQueueAdapter
{
    ValueTask CancelCustomerJobsAsync(Guid customerId, CancellationToken cancellationToken);
}

public enum ExternalConsentRevocationStatus { Succeeded, AlreadyRevoked, PendingManualAction, Partial, Failed }

public sealed record ExternalConsentRevocationEvidence(
    string Operation,
    string Endpoint,
    int? StatusCode,
    string? RequestId,
    string ResponseSha256);

public sealed record ExternalConsentRevocationResult(
    ExternalConsentRevocationStatus Status,
    string? EvidenceReference,
    IReadOnlyCollection<ExternalConsentRevocationEvidence> Evidence,
    string Detail)
{
    public bool Succeeded => Status is ExternalConsentRevocationStatus.Succeeded or ExternalConsentRevocationStatus.AlreadyRevoked;
}

public interface IExternalConsentRevocationAdapter
{
    ValueTask<ExternalConsentRevocationResult> RevokeAsync(Guid customerId, CancellationToken cancellationToken);
}

public sealed record PhysicalDeletionResult(bool Succeeded, string? EvidenceReference, IReadOnlyDictionary<string, long> AfterCounts, string Detail);

public interface IOffboardingJobPublisher
{
    ValueTask PublishAsync(Guid jobId, CancellationToken cancellationToken);
}

public sealed record OnboardingReplayNonce(string Nonce, DateTimeOffset ExpiresAt, DateTimeOffset ConsumedAt);

public sealed class CustomerOffboardingService(
    ICustomerOffboardingStore store,
    ICustomerQueueAdapter queue,
    IExternalConsentRevocationAdapter consent,
    TimeProvider timeProvider)
{
    public async ValueTask<CustomerDeletionRecord> RequestAsync(Guid customerId, string requester, DateTimeOffset retentionExpiresAt, CancellationToken cancellationToken)
    {
        await EnsureNoLegalHoldAsync(customerId, cancellationToken);
        if (await store.GetDeletionAsync(customerId, cancellationToken) is not null) throw new DomainConflictException("An offboarding request already exists.");
        var deletion = new CustomerDeletionRecord(Guid.NewGuid(), customerId, requester, timeProvider.GetUtcNow(), retentionExpiresAt);
        await store.SaveDeletionAsync(deletion, cancellationToken);
        return deletion;
    }

    public async ValueTask<CustomerDeletionRecord> ApproveAsync(Guid customerId, string approver, CancellationToken cancellationToken)
    {
        await EnsureNoLegalHoldAsync(customerId, cancellationToken);
        var deletion = await store.GetDeletionAsync(customerId, cancellationToken) ?? throw new DomainConflictException("Offboarding request does not exist.");
        deletion.Approve(approver, timeProvider.GetUtcNow());
        await store.SaveDeletionAsync(deletion, cancellationToken);
        return deletion;
    }

    public async ValueTask ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var deletion = await store.GetDeletionJobAsync(jobId, cancellationToken) ?? throw new DomainConflictException("Offboarding job does not exist.");
        try
        {
            await EnsureNoLegalHoldAsync(deletion.CustomerId, cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (deletion.Status == DeletionStatus.Approved && now < deletion.RetentionExpiresAt)
            {
                deletion.WaitForRetention(now);
                await store.SaveDeletionAsync(deletion, cancellationToken);
                return;
            }
            if (deletion.Status == DeletionStatus.PendingRetentionExpiry)
            {
                if (now < deletion.RetentionExpiresAt) return;
                deletion.MarkRetentionReady(now);
            }
            if (deletion.Status == DeletionStatus.Completed) return;
            var beforeCounts = await store.CountTenantDataAsync(deletion.CustomerId, cancellationToken);
            deletion.Start(now, beforeCounts);
            await store.SaveDeletionAsync(deletion, cancellationToken);

            var revocation = await consent.RevokeAsync(deletion.CustomerId, cancellationToken);
            if (!revocation.Succeeded || string.IsNullOrWhiteSpace(revocation.EvidenceReference))
                throw new DomainConflictException($"External consent revocation was not verified: {revocation.Detail}");

            await queue.CancelCustomerJobsAsync(deletion.CustomerId, cancellationToken);
            var physical = await store.DeleteTenantDataAsync(deletion.CustomerId, cancellationToken);
            if (!physical.Succeeded || string.IsNullOrWhiteSpace(physical.EvidenceReference))
                throw new DomainConflictException($"Physical deletion was not verified: {physical.Detail}");
            deletion.Complete(timeProvider.GetUtcNow(), physical.AfterCounts, revocation.EvidenceReference, physical.EvidenceReference);
            await store.SaveDeletionAsync(deletion, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            deletion.Fail(exception.Message);
            await store.SaveDeletionAsync(deletion, cancellationToken);
            throw;
        }
    }

    private async ValueTask EnsureNoLegalHoldAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var legalHold = await store.GetLegalHoldAsync(customerId, cancellationToken);
        if (legalHold?.IsActive == true) throw new LegalHoldException(customerId);
    }
}

public sealed class LegalHoldException(Guid customerId)
    : InvalidOperationException($"Customer '{customerId:D}' cannot be deleted while a legal hold is active.");