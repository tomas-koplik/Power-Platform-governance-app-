using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ppgsm.Core.Domain;

namespace Ppgsm.Data;

public interface ICurrentTenant
{
    TenantContext? Value { get; }
}

public sealed class CurrentTenant : ICurrentTenant
{
    public TenantContext? Value { get; private set; }

    public void Set(TenantContext tenant)
    {
        if (Value is not null && Value != tenant) throw new InvalidOperationException("Tenant context cannot change during a scope.");
        Value = tenant;
    }
}

public sealed class PpgsmDbContext(DbContextOptions<PpgsmDbContext> options, ICurrentTenant currentTenant) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<TenantConnection> TenantConnections => Set<TenantConnection>();
    public DbSet<TenantCapability> TenantCapabilities => Set<TenantCapability>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<SnapshotSection> SnapshotSections => Set<SnapshotSection>();
    public DbSet<RawEvidenceReference> RawEvidenceReferences => Set<RawEvidenceReference>();
    public DbSet<CollectorCheckpointRecord> CollectorCheckpoints => Set<CollectorCheckpointRecord>();
    public DbSet<CollectorProgressRecord> CollectorProgress => Set<CollectorProgressRecord>();
    public DbSet<TenantSettingsEvidence> TenantSettingsEvidence => Set<TenantSettingsEvidence>();
    public DbSet<EnvironmentEvidence> EnvironmentEvidence => Set<EnvironmentEvidence>();
    public DbSet<DlpPolicyEvidence> DlpPolicyEvidence => Set<DlpPolicyEvidence>();
    public DbSet<SnapshotEnvironmentScope> SnapshotEnvironmentScopes => Set<SnapshotEnvironmentScope>();
    public DbSet<Finding> Findings => Set<Finding>();
    public DbSet<PocApproval> PocApprovals => Set<PocApproval>();
    public DbSet<GovernanceException> GovernanceExceptions => Set<GovernanceException>();
    public DbSet<ExportJob> ExportJobs => Set<ExportJob>();
    public DbSet<RemediationProposal> RemediationProposals => Set<RemediationProposal>();
    public DbSet<SnapshotJobRecord> SnapshotJobs => Set<SnapshotJobRecord>();
    public DbSet<CustomerLegalHold> CustomerLegalHolds => Set<CustomerLegalHold>();
    public DbSet<CustomerDeletionRecord> CustomerDeletions => Set<CustomerDeletionRecord>();
    public DbSet<OnboardingReplayNonce> OnboardingReplayNonces => Set<OnboardingReplayNonce>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(value => value.CustomerId);
            entity.Property(value => value.Name).HasMaxLength(200);
            entity.Property(value => value.Region).HasMaxLength(50);
            entity.HasIndex(value => value.EntraTenantId).IsUnique();
        });
        modelBuilder.Entity<TenantMembership>(entity =>
        {
            entity.HasKey(value => value.TenantMembershipId);
            entity.Ignore(value => value.Subject);
            entity.HasIndex(value => new { value.CustomerId, value.SubjectTenantId, value.SubjectObjectId }).IsUnique();
        });
        modelBuilder.Entity<TenantConnection>(entity =>
        {
            entity.HasKey(value => value.ConnectionId);
            entity.HasIndex(value => value.CustomerId).IsUnique();
        });
        modelBuilder.Entity<TenantCapability>(entity =>
        {
            entity.HasKey(value => value.TenantCapabilityId);
            entity.Property(value => value.Endpoint).HasMaxLength(500);
            entity.Property(value => value.Identity).HasMaxLength(200);
            entity.Property(value => value.Detail).HasMaxLength(1000);
            entity.HasIndex(value => new { value.CustomerId, value.ConnectionId, value.Endpoint, value.Identity }).IsUnique();
        });
        modelBuilder.Entity<Snapshot>(entity =>
        {
            entity.HasKey(value => value.SnapshotId);
            entity.Property(value => value.IdempotencyKey).HasMaxLength(200);
            entity.Property(value => value.TriggeredBy).HasMaxLength(256);
            entity.HasIndex(value => new { value.CustomerId, value.IdempotencyKey }).IsUnique();
            entity.HasIndex(value => new { value.CustomerId, value.RequestedAt });
            entity.HasMany(value => value.Sections).WithOne().HasForeignKey(value => value.SnapshotId).OnDelete(DeleteBehavior.Restrict);
            entity.Navigation(value => value.Sections).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        modelBuilder.Entity<SnapshotSection>(entity =>
        {
            entity.HasKey(value => value.SnapshotSectionId);
            entity.Property(value => value.SectionKey).HasMaxLength(100);
            entity.HasIndex(value => new { value.CustomerId, value.SnapshotId, value.SectionKey }).IsUnique();
        });
        modelBuilder.Entity<RawEvidenceReference>(entity =>
        {
            entity.HasKey(value => value.RawEvidenceReferenceId);
            entity.Property(value => value.StoragePath).HasMaxLength(500);
            entity.Property(value => value.SectionKey).HasMaxLength(100);
            entity.Property(value => value.ContentHash).HasMaxLength(128);
            entity.Property(value => value.MediaType).HasMaxLength(100);
            entity.Property(value => value.ApiVersion).HasMaxLength(100);
            entity.Property(value => value.LifecycleConfidence).HasConversion<string>().HasMaxLength(32);
            entity.Property(value => value.CollectorId).HasMaxLength(100);
            entity.Property(value => value.CollectorVersion).HasMaxLength(40);
            entity.Property(value => value.ParserSchemaVersion).HasMaxLength(40);
            entity.Property(value => value.PocReference).HasMaxLength(100);
            entity.Property(value => value.Method).HasMaxLength(16);
            entity.Property(value => value.SanitizedUri).HasMaxLength(4000);
            entity.Property(value => value.TokenResource).HasMaxLength(500);
            entity.Property(value => value.AuthMode).HasConversion<string>().HasMaxLength(32);
            entity.Property(value => value.PrincipalIdentityBasis).HasMaxLength(100);
            entity.Property(value => value.RequestId).HasMaxLength(200);
            entity.Property(value => value.RedactedHeaders).HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<Dictionary<string, string>>(value, (JsonSerializerOptions?)null) ?? new());
            entity.Property(value => value.CompletenessRationale).HasMaxLength(1000);
            entity.HasIndex(value => new { value.CustomerId, value.SnapshotId });
            entity.HasIndex(value => new { value.CustomerId, value.SnapshotId, value.SectionKey });
        });
        modelBuilder.Entity<CollectorCheckpointRecord>(entity =>
        {
            entity.HasKey(value => new { value.CustomerId, value.SnapshotId, value.SectionKey });
            entity.Property(value => value.SectionKey).HasMaxLength(100);
            entity.Property(value => value.ContinuationToken).HasMaxLength(4000);
            entity.Property(value => value.EvidenceIdsJson).HasMaxLength(-1);
            entity.HasIndex(value => new { value.CustomerId, value.UpdatedAt });
        });
        modelBuilder.Entity<CollectorProgressRecord>(entity =>
        {
            entity.HasKey(value => value.CollectorProgressRecordId);
            entity.Property(value => value.CollectorProgressRecordId).ValueGeneratedOnAdd();
            entity.Property(value => value.SectionKey).HasMaxLength(100);
            entity.Property(value => value.Status).HasMaxLength(32);
            entity.Property(value => value.Coverage).HasMaxLength(32);
            entity.Property(value => value.Message).HasMaxLength(2000);
            entity.HasIndex(value => new { value.CustomerId, value.SnapshotId, value.SectionKey, value.OccurredAt });
        });
        modelBuilder.Entity<TenantSettingsEvidence>(entity =>
        {
            entity.HasKey(value => value.TenantSettingsEvidenceId);
            entity.Property(value => value.KnownSettings).HasConversion(JsonDocumentValueConverter.Instance);
        });
        modelBuilder.Entity<EnvironmentEvidence>(entity =>
        {
            entity.HasKey(value => value.EnvironmentEvidenceId);
            entity.Property(value => value.Properties).HasConversion(JsonDocumentValueConverter.Instance);
            entity.HasIndex(value => new { value.CustomerId, value.SnapshotId, value.EnvironmentId }).IsUnique();
        });
        modelBuilder.Entity<DlpPolicyEvidence>(entity =>
        {
            entity.HasKey(value => value.DlpPolicyEvidenceId);
            entity.Property(value => value.PolicyId).HasMaxLength(450);
            entity.Property(value => value.DisplayName).HasMaxLength(500);
            entity.Property(value => value.Properties).HasConversion(JsonDocumentValueConverter.Instance);
            entity.HasIndex(value => new { value.CustomerId, value.SnapshotId, value.PolicyId }).IsUnique();
        });
        modelBuilder.Entity<SnapshotEnvironmentScope>(entity =>
        {
            entity.HasKey(value => value.SnapshotId);
            entity.Property(value => value.RequestedEnvironmentIdsJson).HasMaxLength(-1);
            entity.Property(value => value.DiscoveredEnvironmentIdsJson).HasMaxLength(-1);
        });
        modelBuilder.Entity<Finding>(entity =>
        {
            entity.HasKey(value => value.FindingId);
            entity.Property(value => value.AreaWeight).HasPrecision(9, 4);
            entity.Property(value => value.ApplicabilityWeight).HasPrecision(9, 4);
            entity.Property(value => value.EvaluatorRatio).HasPrecision(9, 4);
            entity.Property(value => value.CatalogVersion).HasMaxLength(40);
            entity.Property(value => value.EvaluatorKey).HasMaxLength(100);
            entity.Property(value => value.EvidenceLinksJson).HasMaxLength(-1);
            entity.Property(value => value.PublicationContentDigest).HasMaxLength(80);
            entity.Property(value => value.EvaluatorVersionsJson).HasMaxLength(-1);
            entity.HasIndex(value => new { value.CustomerId, value.SnapshotId, value.RuleId }).IsUnique();
        });
        modelBuilder.Entity<PocApproval>(entity =>
        {
            entity.HasKey(value => value.PocApprovalId);
            entity.Property(value => value.RuleId).HasMaxLength(100);
            entity.Property(value => value.Identity).HasMaxLength(200);
            entity.Property(value => value.ApiVersion).HasMaxLength(100);
            entity.Property(value => value.ApprovedBy).HasMaxLength(200);
            entity.HasIndex(value => new { value.CustomerId, value.RuleId, value.Identity, value.ApiVersion, value.ExpiresAt });
        });
        modelBuilder.Entity<GovernanceException>(entity =>
        {
            entity.HasKey(value => value.ExceptionId);
            entity.HasIndex(value => new { value.CustomerId, value.FindingId, value.ExpiresAt });
        });
        modelBuilder.Entity<ExportJob>(entity =>
        {
            entity.HasKey(value => value.ExportJobId);
            entity.Property(value => value.DownloadUrl).HasMaxLength(500);
            entity.Property(value => value.FailureReason).HasMaxLength(2000);
            entity.Property(value => value.ArtifactContentHash).HasMaxLength(80);
            entity.Property(value => value.ArtifactMediaType).HasMaxLength(100);
            entity.Property(value => value.ArtifactStorageETag).HasMaxLength(200);
            entity.HasIndex(value => new { value.CustomerId, value.CreatedAt });
            entity.HasIndex(value => new { value.Status, value.CreatedAt });
        });
        modelBuilder.Entity<RemediationProposal>(entity =>
        {
            entity.HasKey(value => value.ProposalId);
            entity.Property(value => value.Script);
            entity.Property(value => value.ProposedBy).HasMaxLength(200);
            entity.Property(value => value.ProposedAt);
            entity.Property(value => value.EvidenceCapturedAt);
            entity.Property(value => value.EvidenceValidUntil);
            entity.Property(value => value.Kind);
            entity.Property(value => value.RuleId).HasMaxLength(100);
            entity.Property(value => value.RuleVersion);
            entity.Property(value => value.CatalogVersion).HasMaxLength(40);
            entity.Property(value => value.TemplateId).HasMaxLength(200);
            entity.Property(value => value.TemplateVersion);
            entity.Property(value => value.EvidenceHash).HasMaxLength(128);
            entity.Property(value => value.ParametersJson);
            entity.Property(value => value.TargetScope).HasMaxLength(500);
            entity.Property(value => value.Verification);
            entity.Property(value => value.Rollback);
            entity.HasIndex(value => new { value.CustomerId, value.SnapshotId, value.FindingId });
        });
        modelBuilder.Entity<SnapshotJobRecord>(entity =>
        {
            entity.HasKey(value => value.JobId);
            entity.Ignore(value => value.Actor);
            entity.Property(value => value.SectionsJson).HasMaxLength(4000);
            entity.Property(value => value.EnvironmentIdsJson).HasMaxLength(4000);
            entity.Property(value => value.FailureReason).HasMaxLength(2000);
            entity.HasIndex(value => new { value.CustomerId, value.Status, value.CreatedAt });
        });
        modelBuilder.Entity<CustomerLegalHold>(entity =>
        {
            entity.HasKey(value => value.CustomerId);
            entity.Property(value => value.Reason).HasMaxLength(1000);
        });
        modelBuilder.Entity<CustomerDeletionRecord>(entity =>
        {
            entity.HasKey(value => value.CustomerId);
            entity.Property(value => value.RequestedBy).HasMaxLength(200);
            entity.Property(value => value.RequestedAt);
            entity.Property(value => value.ApprovedBy).HasMaxLength(200);
            entity.Property(value => value.Detail).HasMaxLength(2000);
            entity.Property(value => value.CertificateId).HasMaxLength(100);
            entity.Property(value => value.ConsentRevocationReference).HasMaxLength(500);
            entity.Property(value => value.PhysicalDeletionReference).HasMaxLength(500);
            entity.HasIndex(value => value.JobId).IsUnique();
            entity.HasIndex(value => new { value.Status, value.RetentionExpiresAt });
        });
        modelBuilder.Entity<OnboardingReplayNonce>(entity =>
        {
            entity.HasKey(value => value.Nonce);
            entity.Property(value => value.Nonce).HasMaxLength(200);
            entity.HasIndex(value => value.ExpiresAt);
        });
        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.HasKey(value => value.AuditId);
            entity.Ignore(value => value.Actor);
            entity.Property(value => value.AuditId).ValueGeneratedOnAdd();
            entity.Property(value => value.Details).HasConversion(JsonDocumentValueConverter.NullableInstance);
                        entity.Property(value => value.Outcome).HasMaxLength(32);
                        entity.Property(value => value.Action).HasMaxLength(300);
            entity.HasIndex(value => new { value.CustomerId, value.Timestamp });
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        VerifyTenantWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        VerifyTenantWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void VerifyTenantWrites()
    {
        var tenant = currentTenant.Value ?? throw new InvalidOperationException("Tenant context is required for data operations.");
        foreach (var entry in ChangeTracker.Entries().Where(value => value.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Metadata.ClrType == typeof(OnboardingReplayNonce)) continue;
            var property = entry.Metadata.FindProperty("CustomerId")
                ?? throw new InvalidOperationException($"Tenant-owned entity '{entry.Metadata.ClrType.Name}' has no CustomerId mapping.");
            var customerId = (Guid)(entry.Property(property.Name).CurrentValue ?? Guid.Empty);
            if (!tenant.IsInternal && customerId != tenant.CustomerId) throw new TenantAccessDeniedException();
        }
    }
}

public sealed class TenantSessionConnectionInterceptor(ICurrentTenant currentTenant) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        var tenant = currentTenant.Value ?? throw new InvalidOperationException("Tenant context is required before opening a SQL connection.");
        if (connection is not SqlConnection sqlConnection) throw new InvalidOperationException("Tenant session interceptor requires SQL Server.");

        await using var command = sqlConnection.CreateCommand();
        command.CommandText = "EXEC sys.sp_set_session_context @key=N'CustomerId', @value=@customerId, @read_only=0; EXEC sys.sp_set_session_context @key=N'IsInternal', @value=@isInternal, @read_only=0;";
        command.Parameters.Add(new SqlParameter("@customerId", tenant.CustomerId));
        command.Parameters.Add(new SqlParameter("@isInternal", tenant.IsInternal));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}