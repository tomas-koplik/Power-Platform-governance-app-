using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ppgsm.Core.Domain;
using Ppgsm.Core.Snapshots;

namespace Ppgsm.Data;

public interface IPpgsmDbContextFactory
{
    PpgsmDbContext Create(TenantContext tenant);
}

public sealed class SqlPpgsmDbContextFactory(string connectionString) : IPpgsmDbContextFactory
{
    public PpgsmDbContext Create(TenantContext tenant)
    {
        var currentTenant = new CurrentTenant();
        currentTenant.Set(tenant);
        var interceptor = new TenantSessionConnectionInterceptor(currentTenant);
        var options = new DbContextOptionsBuilder<PpgsmDbContext>()
            .UseSqlServer(connectionString)
            .AddInterceptors(interceptor)
            .Options;
        return new PpgsmDbContext(options, currentTenant);
    }
}

public sealed class SqlDurableStore(
    IPpgsmDbContextFactory contexts,
    IRawEvidenceContentStore evidenceContent,
    IExportArtifactStore exportArtifacts,
    TimeProvider timeProvider) :
    ICustomerStore,
    ITenantMembershipStore,
    ITenantConnectionStore,
    ISnapshotStore,
    IRawEvidenceAuthorizationStore,
    ISnapshotEvidenceSink,
    ICollectorCheckpointStore,
    ISectionProgressSink,
    IGovernanceStore,
    IEvaluationEvidenceStore,
    IEvidenceProjectionStore,
    IPocApprovalStore,
    ISnapshotJobStore,
    ICustomerQueueAdapter,
    ICustomerOffboardingStore,
    IAuditSink
{
    private static readonly SubjectIdentity ServiceIdentity = SubjectIdentity.Create(
        Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff1"),
        Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffff2"));

    public async ValueTask<Customer?> FindCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.Customers.AsNoTracking().SingleOrDefaultAsync(value => value.CustomerId == customerId, cancellationToken);
    }

    public async ValueTask<Customer> CreateCustomerAsync(string name, Guid entraTenantId, string region, SubjectIdentity creator, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Customer name is required.", nameof(name));
        if (entraTenantId == Guid.Empty) throw new ArgumentException("Entra tenant ID is required.", nameof(entraTenantId));

        var customerId = Guid.NewGuid();
        await using var db = CustomerContext(customerId);
        if (await db.Customers.AnyAsync(value => value.EntraTenantId == entraTenantId, cancellationToken))
        {
            throw new DomainConflictException("Entra tenant is already registered.");
        }

        var customer = new Customer(customerId, name.Trim(), entraTenantId, region.Trim(), CustomerStatus.Pending, timeProvider.GetUtcNow());
        db.Customers.Add(customer);
        db.TenantMemberships.Add(new(Guid.NewGuid(), customerId, creator.TenantId, creator.ObjectId, MembershipRole.Consultant, timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return customer;
    }

    public async ValueTask<TenantMembership?> FindAsync(SubjectIdentity subject, Guid customerId, CancellationToken cancellationToken)
    {
        await using var db = InternalContext(customerId);
        return await db.TenantMemberships.AsNoTracking().SingleOrDefaultAsync(value =>
            value.CustomerId == customerId && value.SubjectTenantId == subject.TenantId && value.SubjectObjectId == subject.ObjectId,
            cancellationToken);
    }

    public async ValueTask<IReadOnlyList<TenantMembership>> ListForSubjectAsync(SubjectIdentity subject, CancellationToken cancellationToken)
    {
        await using var db = InternalContext(Guid.Empty);
        return await db.TenantMemberships.AsNoTracking()
            .Where(value => value.SubjectTenantId == subject.TenantId && value.SubjectObjectId == subject.ObjectId)
            .OrderBy(value => value.CustomerId)
            .ToArrayAsync(cancellationToken);
    }

    public async ValueTask<TenantMembership> GrantAsync(Guid customerId, SubjectIdentity subject, MembershipRole role, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        var current = await db.TenantMemberships.SingleOrDefaultAsync(value => value.CustomerId == customerId &&
            value.SubjectTenantId == subject.TenantId && value.SubjectObjectId == subject.ObjectId, cancellationToken);
        if (current is not null) db.TenantMemberships.Remove(current);
        var membership = new TenantMembership(current?.TenantMembershipId ?? Guid.NewGuid(), customerId, subject.TenantId, subject.ObjectId, role, current?.CreatedAt ?? timeProvider.GetUtcNow());
        db.TenantMemberships.Add(membership);
        await db.SaveChangesAsync(cancellationToken);
        return membership;
    }

    public async ValueTask<TenantConnection?> FindAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.TenantConnections.AsNoTracking().SingleOrDefaultAsync(value => value.CustomerId == customerId, cancellationToken);
    }

    public async ValueTask<TenantConnection> SaveAsync(TenantConnection connection, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(connection.CustomerId);
        var current = await db.TenantConnections.SingleOrDefaultAsync(value => value.CustomerId == connection.CustomerId, cancellationToken);
        if (current is not null) db.TenantConnections.Remove(current);
        db.TenantConnections.Add(connection);
        await db.SaveChangesAsync(cancellationToken);
        return connection;
    }

    public async ValueTask<IReadOnlyList<TenantCapability>> ListCapabilitiesAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.TenantCapabilities.AsNoTracking().Where(value => value.CustomerId == customerId)
            .OrderBy(value => value.Endpoint).ThenBy(value => value.Identity).ToArrayAsync(cancellationToken);
    }

    public async ValueTask ReplaceCapabilitiesAsync(Guid customerId, Guid connectionId, IReadOnlyCollection<TenantCapability> capabilities, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        await db.TenantCapabilities.Where(value => value.CustomerId == customerId && value.ConnectionId == connectionId).ExecuteDeleteAsync(cancellationToken);
        db.TenantCapabilities.AddRange(capabilities);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<Snapshot?> FindByIdAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.Snapshots.Include(value => value.Sections).AsNoTracking()
            .SingleOrDefaultAsync(value => value.CustomerId == customerId && value.SnapshotId == snapshotId, cancellationToken);
    }

    public async ValueTask<Snapshot?> FindByIdempotencyKeyAsync(Guid customerId, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.Snapshots.Include(value => value.Sections).AsNoTracking()
            .SingleOrDefaultAsync(value => value.CustomerId == customerId && value.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<Snapshot>> ListAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.Snapshots.Include(value => value.Sections).AsNoTracking()
            .Where(value => value.CustomerId == customerId).OrderByDescending(value => value.RequestedAt).ToArrayAsync(cancellationToken);
    }

    public async ValueTask AddAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(snapshot.CustomerId);
        db.Snapshots.Add(snapshot);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask SaveAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(snapshot.CustomerId);
        db.Snapshots.Update(snapshot);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<AuthorizedRawEvidence?> OpenAuthorizedAsync(Guid customerId, Guid snapshotId, Guid evidenceId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        var reference = await db.RawEvidenceReferences.AsNoTracking().SingleOrDefaultAsync(value =>
            value.CustomerId == customerId && value.SnapshotId == snapshotId && value.RawEvidenceReferenceId == evidenceId, cancellationToken);
        if (reference is null) return null;
        var content = await evidenceContent.OpenReadAsync(reference.StoragePath, cancellationToken);
        return content is null ? null : new(reference, content);
    }

    public async ValueTask WriteRawAsync(RawEvidenceReference evidence, Stream content, CancellationToken cancellationToken)
    {
        await evidenceContent.WriteAsync(evidence, content, cancellationToken);
        await using var db = CustomerContext(evidence.CustomerId);
        db.RawEvidenceReferences.Add(evidence);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<CollectorCheckpoint?> ReadAsync(Guid customerId, Guid snapshotId, string sectionKey, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        var record = await db.CollectorCheckpoints.AsNoTracking().SingleOrDefaultAsync(value =>
            value.CustomerId == customerId && value.SnapshotId == snapshotId && value.SectionKey == sectionKey, cancellationToken);
        if (record is null) return null;
        var evidenceIds = JsonSerializer.Deserialize<Guid[]>(record.EvidenceIdsJson) ?? [];
        var evidence = await db.RawEvidenceReferences.AsNoTracking()
            .Where(value => evidenceIds.Contains(value.RawEvidenceReferenceId)).ToArrayAsync(cancellationToken);
        return new(record.CustomerId, record.SnapshotId, record.SectionKey, record.ContinuationToken,
            record.CompletedPages, record.ItemCount, evidence, record.UpdatedAt);
    }

    public async ValueTask WriteAsync(CollectorCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(checkpoint.CustomerId);
        var record = await db.CollectorCheckpoints.SingleOrDefaultAsync(value => value.CustomerId == checkpoint.CustomerId &&
            value.SnapshotId == checkpoint.SnapshotId && value.SectionKey == checkpoint.SectionKey, cancellationToken);
        if (record is not null) db.CollectorCheckpoints.Remove(record);
        db.CollectorCheckpoints.Add(new(checkpoint.CustomerId, checkpoint.SnapshotId, checkpoint.SectionKey, checkpoint.ContinuationToken,
            checkpoint.CompletedPages, checkpoint.ItemCount,
            JsonSerializer.Serialize(checkpoint.Evidence.Select(value => value.RawEvidenceReferenceId)), checkpoint.UpdatedAt));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask CompleteAsync(Guid customerId, Guid snapshotId, string sectionKey, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        await db.CollectorCheckpoints.Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId && value.SectionKey == sectionKey)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async ValueTask PublishAsync(SectionProgress progress, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(progress.CustomerId);
        db.CollectorProgress.Add(new(0, progress.CustomerId, progress.SnapshotId, progress.SectionKey, progress.Status.ToString(),
            progress.CompletedPages, progress.ItemCount, progress.Coverage?.ToString(), progress.Message, progress.OccurredAt));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await using var db = CustomerContext(auditEvent.CustomerId);
        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<Finding>> ListFindingsAsync(Guid customerId, Guid snapshotId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        var excepted = await db.GovernanceExceptions.AsNoTracking().Where(value => value.CustomerId == customerId && value.ExpiresAt > now)
            .Select(value => value.FindingId).ToArrayAsync(cancellationToken);
        var findings = await db.Findings.AsNoTracking().Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId).ToArrayAsync(cancellationToken);
        return findings.Select(value => excepted.Contains(value.FindingId) ? value with { Status = FindingStatus.Excepted } : value).ToArray();
    }

    public async ValueTask<Finding?> FindFindingAsync(Guid customerId, Guid snapshotId, Guid findingId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.Findings.AsNoTracking().SingleOrDefaultAsync(value => value.CustomerId == customerId &&
            value.SnapshotId == snapshotId && value.FindingId == findingId, cancellationToken);
    }

    public async ValueTask<RawEvidenceReference?> FindEvidenceByHashAsync(Guid customerId, Guid snapshotId, string evidenceHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(evidenceHash)) return null;
        await using var db = CustomerContext(customerId);
        return await db.RawEvidenceReferences.AsNoTracking().SingleOrDefaultAsync(value => value.CustomerId == customerId &&
            value.SnapshotId == snapshotId && value.ContentHash == evidenceHash, cancellationToken);
    }

    public async ValueTask ReplaceFindingsAsync(Guid customerId, Guid snapshotId, IReadOnlyCollection<Finding> findings, CancellationToken cancellationToken)
    {
        if (findings.Any(value => value.CustomerId != customerId || value.SnapshotId != snapshotId)) throw new TenantAccessDeniedException();
        await using var db = CustomerContext(customerId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Findings.Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId).ExecuteDeleteAsync(cancellationToken);
        db.Findings.AddRange(findings);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<PocApproval> AddPocApprovalAsync(PocApproval approval, CancellationToken cancellationToken)
    {
        if (approval.ExpiresAt <= approval.ApprovedAt) throw new ArgumentException("PoC approval expiry must be after approval.", nameof(approval));
        await using var db = CustomerContext(approval.CustomerId);
        var evidenceMatches = await db.RawEvidenceReferences.AsNoTracking().AnyAsync(value =>
            value.CustomerId == approval.CustomerId && value.RawEvidenceReferenceId == approval.EvidenceReferenceId &&
            value.PrincipalIdentityBasis == approval.Identity && value.ApiVersion == approval.ApiVersion, cancellationToken);
        if (!evidenceMatches) throw new TenantAccessDeniedException();
        db.PocApprovals.Add(approval);
        await db.SaveChangesAsync(cancellationToken);
        return approval;
    }

    public async ValueTask<IReadOnlySet<string>> GetApprovedRuleIdsAsync(Guid customerId, string identity, string apiVersion,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        var ruleIds = await db.PocApprovals.AsNoTracking().Where(value => value.CustomerId == customerId &&
            value.Identity == identity && value.ApiVersion == apiVersion && value.ApprovedAt <= now && value.ExpiresAt > now)
            .Select(value => value.RuleId).Distinct().ToArrayAsync(cancellationToken);
        return ruleIds.ToHashSet(StringComparer.Ordinal);
    }

    public async ValueTask<IReadOnlyCollection<EvaluationEvidencePayload>> LoadEvaluationEvidenceAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        var references = await db.RawEvidenceReferences.AsNoTracking()
            .Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId)
            .OrderBy(value => value.SectionKey).ThenBy(value => value.PageNumber).ToArrayAsync(cancellationToken);
        var result = new List<EvaluationEvidencePayload>(references.Length);
        foreach (var reference in references)
        {
            await using var content = await evidenceContent.OpenReadAsync(reference.StoragePath, cancellationToken)
                ?? throw new InvalidOperationException($"Raw evidence '{reference.RawEvidenceReferenceId}' is unavailable.");
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            result.Add(new(reference, buffer.ToArray()));
        }
        return result;
    }

    public async ValueTask SaveEnvironmentScopeAsync(SnapshotEnvironmentScope scope, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(scope.CustomerId);
        await db.SnapshotEnvironmentScopes.Where(value => value.CustomerId == scope.CustomerId && value.SnapshotId == scope.SnapshotId).ExecuteDeleteAsync(cancellationToken);
        db.SnapshotEnvironmentScopes.Add(scope);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask SaveNormalizedEvidenceAsync(Guid customerId, Guid snapshotId, NormalizedEvidenceProjection projection, CancellationToken cancellationToken)
    {
        if (projection.TenantSettings.Any(value => value.CustomerId != customerId || value.SnapshotId != snapshotId) ||
            projection.Environments.Any(value => value.CustomerId != customerId || value.SnapshotId != snapshotId) ||
            projection.DlpPolicies.Any(value => value.CustomerId != customerId || value.SnapshotId != snapshotId)) throw new TenantAccessDeniedException();
        await using var db = CustomerContext(customerId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.TenantSettingsEvidence.Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId).ExecuteDeleteAsync(cancellationToken);
        await db.EnvironmentEvidence.Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId).ExecuteDeleteAsync(cancellationToken);
        await db.DlpPolicyEvidence.Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId).ExecuteDeleteAsync(cancellationToken);
        db.TenantSettingsEvidence.AddRange(projection.TenantSettings);
        db.EnvironmentEvidence.AddRange(projection.Environments);
        db.DlpPolicyEvidence.AddRange(projection.DlpPolicies);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<EvidenceMetadataPage> ListEvidenceMetadataAsync(Guid customerId, Guid snapshotId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        var query = db.RawEvidenceReferences.AsNoTracking().Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(value => value.SectionKey).ThenBy(value => value.PageNumber).ThenBy(value => value.CapturedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);
        return new(items, page, pageSize, total);
    }

    public async ValueTask<IReadOnlyCollection<TenantSettingsEvidence>> ListTenantSettingsAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.TenantSettingsEvidence.AsNoTracking().Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId).ToArrayAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyCollection<EnvironmentEvidence>> ListEnvironmentsAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.EnvironmentEvidence.AsNoTracking().Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId).OrderBy(value => value.DisplayName).ToArrayAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyCollection<DlpPolicyEvidence>> ListDlpPoliciesAsync(Guid customerId, Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.DlpPolicyEvidence.AsNoTracking().Where(value => value.CustomerId == customerId && value.SnapshotId == snapshotId).OrderBy(value => value.DisplayName).ToArrayAsync(cancellationToken);
    }

    public async ValueTask<GovernanceException> AddExceptionAsync(GovernanceException exception, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(exception.CustomerId);
        db.GovernanceExceptions.Add(exception);
        await db.SaveChangesAsync(cancellationToken);
        return exception;
    }

    public async ValueTask<ExportJob> AddExportAsync(ExportJob job, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(job.CustomerId);
        db.ExportJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async ValueTask<ExportJob?> FindExportAsync(Guid customerId, Guid exportJobId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.ExportJobs.AsNoTracking().SingleOrDefaultAsync(value => value.CustomerId == customerId && value.ExportJobId == exportJobId, cancellationToken);
    }

    public async ValueTask<IReadOnlyCollection<Guid>> ListQueuedExportsAsync(CancellationToken cancellationToken)
    {
        await using var db = InternalContext(Guid.Empty);
        return await db.ExportJobs.AsNoTracking().Where(value => value.Status == ExportJobStatus.Queued).OrderBy(value => value.CreatedAt)
            .Select(value => value.ExportJobId).Take(100).ToArrayAsync(cancellationToken);
    }

    public async ValueTask<ExportJob?> ClaimExportAsync(Guid exportJobId, CancellationToken cancellationToken)
    {
        await using var lookup = InternalContext(Guid.Empty);
        var job = await lookup.ExportJobs.AsNoTracking().SingleOrDefaultAsync(value => value.ExportJobId == exportJobId, cancellationToken);
        if (job is null || job.Status != ExportJobStatus.Queued) return null;
        await using var db = CustomerContext(job.CustomerId);
        var updated = await db.ExportJobs.Where(value => value.CustomerId == job.CustomerId && value.ExportJobId == exportJobId && value.Status == ExportJobStatus.Queued)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Status, ExportJobStatus.Running)
                .SetProperty(value => value.UpdatedAt, timeProvider.GetUtcNow()), cancellationToken);
        return updated == 1 ? job with { Status = ExportJobStatus.Running, UpdatedAt = timeProvider.GetUtcNow() } : null;
    }

    public async ValueTask CompleteExportAsync(Guid exportJobId, string artifactPath, DateTimeOffset downloadExpiresAt,
        ExportArtifactDescriptor artifact, CancellationToken cancellationToken) =>
        await UpdateExportAsync(exportJobId, ExportJobStatus.Completed, artifactPath, null, downloadExpiresAt, artifact, cancellationToken);

    public async ValueTask FailExportAsync(Guid exportJobId, string reason, CancellationToken cancellationToken) =>
        await UpdateExportAsync(exportJobId, ExportJobStatus.Failed, null, reason, null, null, cancellationToken);

    public async ValueTask<RemediationProposal> AddProposalAsync(RemediationProposal proposal, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(proposal.CustomerId);
        db.RemediationProposals.Add(proposal);
        await db.SaveChangesAsync(cancellationToken);
        return proposal;
    }

    public async ValueTask<RemediationProposal?> FindProposalAsync(Guid customerId, Guid proposalId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.RemediationProposals.SingleOrDefaultAsync(value => value.CustomerId == customerId && value.ProposalId == proposalId, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<RemediationProposal>> ListProposalsAsync(Guid customerId, RemediationProposalStatus? status, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        var query = db.RemediationProposals.AsNoTracking().Where(value => value.CustomerId == customerId);
        if (status is not null) query = query.Where(value => value.Status == status);
        return await query.OrderByDescending(value => value.ProposedAt).ThenBy(value => value.ProposalId).ToArrayAsync(cancellationToken);
    }

    public async ValueTask SaveProposalAsync(RemediationProposal proposal, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(proposal.CustomerId);
        db.RemediationProposals.Update(proposal);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<SnapshotJobRecord?> LoadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var db = InternalContext(Guid.Empty);
        return await db.SnapshotJobs.AsNoTracking().SingleOrDefaultAsync(value => value.JobId == jobId, cancellationToken);
    }

    public async ValueTask AddAsync(SnapshotJobRecord job, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(job.CustomerId);
        if (await db.SnapshotJobs.AnyAsync(value => value.SnapshotId == job.SnapshotId, cancellationToken)) return;
        db.SnapshotJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyCollection<Guid>> ListQueuedAsync(CancellationToken cancellationToken)
    {
        await using var db = InternalContext(Guid.Empty);
        return await db.SnapshotJobs.AsNoTracking()
            .Where(value => value.Status == SnapshotJobStatus.Queued)
            .OrderBy(value => value.CreatedAt)
            .Select(value => value.JobId)
            .Take(1000)
            .ToArrayAsync(cancellationToken);
    }

    public async ValueTask MarkRunningAsync(Guid jobId, CancellationToken cancellationToken) =>
        await UpdateJobAsync(jobId, SnapshotJobStatus.Running, null, null, cancellationToken);

    public async ValueTask CompleteAsync(Guid jobId, IReadOnlyCollection<SnapshotSection> sections, CancellationToken cancellationToken) =>
        await UpdateJobAsync(jobId, SnapshotJobStatus.Completed, null, sections, cancellationToken);

    public async ValueTask FailAsync(Guid jobId, string reason, CancellationToken cancellationToken) =>
        await UpdateJobAsync(jobId, SnapshotJobStatus.Failed, reason, null, cancellationToken);

    public async ValueTask CancelCustomerJobsAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        await db.SnapshotJobs
            .Where(value => value.CustomerId == customerId && (value.Status == SnapshotJobStatus.Queued || value.Status == SnapshotJobStatus.Running))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, SnapshotJobStatus.Cancelled)
                .SetProperty(value => value.UpdatedAt, timeProvider.GetUtcNow())
                .SetProperty(value => value.FailureReason, "Cancelled by customer offboarding."), cancellationToken);
    }

    public async ValueTask<CustomerLegalHold?> GetLegalHoldAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.CustomerLegalHolds.AsNoTracking().SingleOrDefaultAsync(value => value.CustomerId == customerId, cancellationToken);
    }

    public async ValueTask<CustomerDeletionRecord?> GetDeletionAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return await db.CustomerDeletions.AsNoTracking().SingleOrDefaultAsync(value => value.CustomerId == customerId, cancellationToken);
    }

    public async ValueTask<CustomerDeletionRecord?> GetDeletionJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var db = InternalContext(Guid.Empty);
        return await db.CustomerDeletions.SingleOrDefaultAsync(value => value.JobId == jobId, cancellationToken);
    }

    public async ValueTask SaveDeletionAsync(CustomerDeletionRecord deletion, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(deletion.CustomerId);
        var current = await db.CustomerDeletions.SingleOrDefaultAsync(value => value.CustomerId == deletion.CustomerId, cancellationToken);
        if (current is not null) db.CustomerDeletions.Remove(current);
        db.CustomerDeletions.Add(deletion);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyCollection<Guid>> ListExecutableDeletionJobsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = InternalContext(Guid.Empty);
        return await db.CustomerDeletions.AsNoTracking()
            .Where(value => value.Status == DeletionStatus.Approved ||
                (value.Status == DeletionStatus.PendingRetentionExpiry && value.RetentionExpiresAt <= now))
            .OrderBy(value => value.RequestedAt)
            .Select(value => value.JobId)
            .Take(100)
            .ToArrayAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyDictionary<string, long>> CountTenantDataAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        return new Dictionary<string, long>
        {
            ["Customers"] = await db.Customers.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["TenantMemberships"] = await db.TenantMemberships.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["TenantConnections"] = await db.TenantConnections.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["TenantCapabilities"] = await db.TenantCapabilities.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["Snapshots"] = await db.Snapshots.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["SnapshotSections"] = await db.SnapshotSections.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["RawEvidenceReferences"] = await db.RawEvidenceReferences.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["CollectorCheckpoints"] = await db.CollectorCheckpoints.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["CollectorProgress"] = await db.CollectorProgress.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["TenantSettingsEvidence"] = await db.TenantSettingsEvidence.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["EnvironmentEvidence"] = await db.EnvironmentEvidence.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["DlpPolicyEvidence"] = await db.DlpPolicyEvidence.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["SnapshotEnvironmentScopes"] = await db.SnapshotEnvironmentScopes.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["Findings"] = await db.Findings.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["GovernanceExceptions"] = await db.GovernanceExceptions.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["ExportJobs"] = await db.ExportJobs.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["RemediationProposals"] = await db.RemediationProposals.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["SnapshotJobs"] = await db.SnapshotJobs.LongCountAsync(value => value.CustomerId == customerId, cancellationToken),
            ["CustomerLegalHolds"] = await db.CustomerLegalHolds.LongCountAsync(value => value.CustomerId == customerId, cancellationToken)
        };
    }

    public async ValueTask<PhysicalDeletionResult> DeleteTenantDataAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var db = CustomerContext(customerId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var paths = await db.RawEvidenceReferences.Where(value => value.CustomerId == customerId).Select(value => value.StoragePath).ToArrayAsync(cancellationToken);
        await db.CollectorProgress.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.CollectorCheckpoints.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.SnapshotJobs.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.GovernanceExceptions.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.RemediationProposals.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.ExportJobs.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.Findings.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.TenantSettingsEvidence.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.EnvironmentEvidence.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.DlpPolicyEvidence.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.SnapshotEnvironmentScopes.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.RawEvidenceReferences.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.SnapshotSections.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.Snapshots.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.TenantConnections.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.TenantCapabilities.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.TenantMemberships.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.CustomerLegalHolds.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await db.Customers.Where(value => value.CustomerId == customerId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await evidenceContent.DeleteCustomerAsync(customerId, paths, cancellationToken);
        await exportArtifacts.DeleteCustomerAsync(customerId, cancellationToken);
        var afterCounts = await CountTenantDataAsync(customerId, cancellationToken);
        return new(afterCounts.Values.All(value => value == 0), $"sql-and-blob:{customerId:N}", afterCounts,
            afterCounts.Values.All(value => value == 0) ? "Tenant rows and referenced evidence were deleted." : "Tenant data remains after deletion.");
    }

    private async ValueTask UpdateJobAsync(Guid jobId, SnapshotJobStatus status, string? reason, IReadOnlyCollection<SnapshotSection>? sections, CancellationToken cancellationToken)
    {
        await using var lookup = InternalContext(Guid.Empty);
        var job = await lookup.SnapshotJobs.AsNoTracking().SingleOrDefaultAsync(value => value.JobId == jobId, cancellationToken)
            ?? throw new DomainConflictException("Snapshot job does not exist.");
        await using var db = CustomerContext(job.CustomerId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SnapshotJobs.Where(value => value.JobId == jobId && value.CustomerId == job.CustomerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.Status, status)
                .SetProperty(value => value.UpdatedAt, timeProvider.GetUtcNow())
                .SetProperty(value => value.FailureReason, reason), cancellationToken);
        var snapshot = await db.Snapshots.Include(value => value.Sections).SingleAsync(value => value.CustomerId == job.CustomerId && value.SnapshotId == job.SnapshotId, cancellationToken);
        if (status == SnapshotJobStatus.Running) snapshot.Start(timeProvider.GetUtcNow());
        else if (status == SnapshotJobStatus.Completed)
        {
            foreach (var section in sections ?? []) snapshot.RecordSection(section);
            snapshot.Complete(timeProvider.GetUtcNow());
        }
        else if (status == SnapshotJobStatus.Failed) snapshot.Fail(reason ?? "Snapshot job failed.", timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async ValueTask UpdateExportAsync(Guid exportJobId, ExportJobStatus status, string? artifactPath, string? reason,
        DateTimeOffset? downloadExpiresAt, ExportArtifactDescriptor? artifact, CancellationToken cancellationToken)
    {
        await using var lookup = InternalContext(Guid.Empty);
        var job = await lookup.ExportJobs.AsNoTracking().SingleOrDefaultAsync(value => value.ExportJobId == exportJobId, cancellationToken)
            ?? throw new DomainConflictException("Export job does not exist.");
        await using var db = CustomerContext(job.CustomerId);
        var updated = await db.ExportJobs.Where(value => value.CustomerId == job.CustomerId && value.ExportJobId == exportJobId && value.Status == ExportJobStatus.Running)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Status, status)
                .SetProperty(value => value.DownloadUrl, artifactPath)
                .SetProperty(value => value.FailureReason, reason)
                .SetProperty(value => value.DownloadExpiresAt, downloadExpiresAt)
                .SetProperty(value => value.ArtifactContentHash, artifact == null ? null : artifact.ContentHash)
                .SetProperty(value => value.ArtifactContentLength, artifact == null ? null : artifact.ContentLength)
                .SetProperty(value => value.ArtifactMediaType, artifact == null ? null : artifact.MediaType)
                .SetProperty(value => value.ArtifactStorageETag, artifact == null ? null : artifact.StorageETag)
                .SetProperty(value => value.UpdatedAt, timeProvider.GetUtcNow()), cancellationToken);
        if (updated != 1) throw new DomainConflictException("Export job is not running.");
    }

    private PpgsmDbContext CustomerContext(Guid customerId) => contexts.Create(new(customerId, ServiceIdentity, MembershipRole.InternalAdmin));
    private PpgsmDbContext InternalContext(Guid customerId) => contexts.Create(new(customerId, ServiceIdentity, MembershipRole.InternalAdmin, IsInternal: true));
}