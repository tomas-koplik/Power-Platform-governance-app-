using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ppgsm.Data.Migrations;

[DbContext(typeof(PpgsmDbContext))]
[Migration("202607140001_DurableSecurityBaseline")]
public sealed class DurableSecurityBaseline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE dbo.Customers (CustomerId uniqueidentifier NOT NULL PRIMARY KEY, Name nvarchar(200) NOT NULL, EntraTenantId uniqueidentifier NOT NULL, Region nvarchar(50) NOT NULL, Status int NOT NULL, CreatedAt datetimeoffset NOT NULL);
CREATE UNIQUE INDEX IX_Customers_EntraTenantId ON dbo.Customers(EntraTenantId);
CREATE TABLE dbo.TenantMemberships (TenantMembershipId uniqueidentifier NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, SubjectTenantId uniqueidentifier NOT NULL, SubjectObjectId uniqueidentifier NOT NULL, Role int NOT NULL, CreatedAt datetimeoffset NOT NULL);
CREATE UNIQUE INDEX IX_TenantMemberships_CompositeSubject ON dbo.TenantMemberships(CustomerId, SubjectTenantId, SubjectObjectId);
CREATE TABLE dbo.TenantConnections (ConnectionId uniqueidentifier NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, Mode int NOT NULL, AppRegistrationId uniqueidentifier NULL, ServicePrincipalObjectId uniqueidentifier NULL, RbacRoleAssignmentId nvarchar(max) NULL, LegacyManagementAppRegistered bit NOT NULL, CertificateThumbprint nvarchar(max) NULL, ConsentGrantedBy nvarchar(max) NULL, ConsentGrantedAt datetimeoffset NULL, Status int NOT NULL, LastValidatedAt datetimeoffset NULL);
CREATE TABLE dbo.Snapshots (SnapshotId uniqueidentifier NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, IdempotencyKey nvarchar(200) NOT NULL, TriggeredBy nvarchar(256) NOT NULL, Mode int NOT NULL, SchemaVersion int NOT NULL, Status int NOT NULL, RequestedAt datetimeoffset NOT NULL, StartedAt datetimeoffset NULL, CompletedAt datetimeoffset NULL, FailureReason nvarchar(max) NULL);
CREATE UNIQUE INDEX IX_Snapshots_Idempotency ON dbo.Snapshots(CustomerId, IdempotencyKey);
CREATE INDEX IX_Snapshots_RequestedAt ON dbo.Snapshots(CustomerId, RequestedAt);
CREATE TABLE dbo.SnapshotSections (SnapshotSectionId uniqueidentifier NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, SnapshotId uniqueidentifier NOT NULL, SectionKey nvarchar(100) NOT NULL, Coverage int NOT NULL, ItemCount int NOT NULL, Reason nvarchar(max) NULL, RecordedAt datetimeoffset NOT NULL, CONSTRAINT FK_SnapshotSections_Snapshots FOREIGN KEY (SnapshotId) REFERENCES dbo.Snapshots(SnapshotId));
CREATE UNIQUE INDEX IX_SnapshotSections_Section ON dbo.SnapshotSections(CustomerId, SnapshotId, SectionKey);
CREATE TABLE dbo.RawEvidenceReferences (RawEvidenceReferenceId uniqueidentifier NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, SnapshotId uniqueidentifier NOT NULL, SectionKey nvarchar(100) NOT NULL, StoragePath nvarchar(500) NOT NULL, ContentHash nvarchar(128) NOT NULL, MediaType nvarchar(100) NOT NULL, ApiVersion nvarchar(100) NOT NULL, CapturedAt datetimeoffset NOT NULL, LifecycleConfidence nvarchar(32) NOT NULL, CollectorId nvarchar(100) NOT NULL, CollectorVersion nvarchar(40) NOT NULL, ParserSchemaVersion nvarchar(40) NOT NULL, PocReference nvarchar(100) NULL, Method nvarchar(16) NOT NULL, SanitizedUri nvarchar(4000) NOT NULL, StatusCode int NOT NULL, TokenResource nvarchar(500) NOT NULL, AuthMode nvarchar(32) NOT NULL, TenantIdentityBasis uniqueidentifier NOT NULL, PrincipalIdentityBasis nvarchar(100) NOT NULL, RequestId nvarchar(200) NULL, RedactedHeaders nvarchar(max) NOT NULL, PageNumber int NOT NULL, AttemptNumber int NOT NULL, PreviousEvidenceId uniqueidentifier NULL, CompletenessRationale nvarchar(1000) NOT NULL);
CREATE INDEX IX_RawEvidenceReferences_Snapshot ON dbo.RawEvidenceReferences(CustomerId, SnapshotId);
CREATE INDEX IX_RawEvidenceReferences_Section ON dbo.RawEvidenceReferences(CustomerId, SnapshotId, SectionKey);
CREATE TABLE dbo.CollectorCheckpoints (CustomerId uniqueidentifier NOT NULL, SnapshotId uniqueidentifier NOT NULL, SectionKey nvarchar(100) NOT NULL, ContinuationToken nvarchar(4000) NULL, CompletedPages int NOT NULL, ItemCount int NOT NULL, EvidenceIdsJson nvarchar(max) NOT NULL, UpdatedAt datetimeoffset NOT NULL, CONSTRAINT PK_CollectorCheckpoints PRIMARY KEY(CustomerId, SnapshotId, SectionKey));
CREATE TABLE dbo.CollectorProgress (CollectorProgressRecordId bigint IDENTITY NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, SnapshotId uniqueidentifier NOT NULL, SectionKey nvarchar(100) NOT NULL, Status nvarchar(32) NOT NULL, CompletedPages int NOT NULL, ItemCount int NOT NULL, Coverage nvarchar(32) NULL, Message nvarchar(2000) NULL, OccurredAt datetimeoffset NOT NULL);
CREATE TABLE dbo.TenantSettingsEvidence (TenantSettingsEvidenceId uniqueidentifier NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, SnapshotId uniqueidentifier NOT NULL, TrialEnvironmentsDisabled bit NULL, DeveloperEnvironmentsRestricted bit NULL, CopilotDataMovementRestricted bit NULL, KnownSettings nvarchar(max) NOT NULL, RawEvidenceReferenceId uniqueidentifier NOT NULL);
CREATE TABLE dbo.EnvironmentEvidence (EnvironmentEvidenceId uniqueidentifier NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, SnapshotId uniqueidentifier NOT NULL, EnvironmentId nvarchar(450) NOT NULL, DisplayName nvarchar(max) NOT NULL, EnvironmentType nvarchar(max) NOT NULL, Region nvarchar(max) NOT NULL, IsDefault bit NOT NULL, IsManaged bit NOT NULL, ProtectionLevel nvarchar(max) NULL, HasDataverse bit NOT NULL, SecurityGroupId uniqueidentifier NULL, Properties nvarchar(max) NOT NULL, RawEvidenceReferenceId uniqueidentifier NOT NULL);
CREATE UNIQUE INDEX IX_EnvironmentEvidence_Environment ON dbo.EnvironmentEvidence(CustomerId, SnapshotId, EnvironmentId);
CREATE TABLE dbo.Findings (FindingId uniqueidentifier NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, SnapshotId uniqueidentifier NOT NULL, RuleId nvarchar(450) NOT NULL, Title nvarchar(max) NOT NULL, Area nvarchar(max) NOT NULL, Severity int NOT NULL, Status int NOT NULL, Scope nvarchar(max) NOT NULL, Observed nvarchar(max) NOT NULL, Interpretation nvarchar(max) NOT NULL, ProposedAction nvarchar(max) NOT NULL, Remediation int NOT NULL, OwnerUpn nvarchar(max) NULL, AreaWeight decimal(9,4) NOT NULL, ApplicabilityWeight decimal(9,4) NOT NULL, EvaluatorRatio decimal(9,4) NOT NULL);
CREATE UNIQUE INDEX IX_Findings_Rule ON dbo.Findings(CustomerId, SnapshotId, RuleId);
CREATE TABLE dbo.GovernanceExceptions (ExceptionId uniqueidentifier NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, FindingId uniqueidentifier NOT NULL, Reason nvarchar(max) NOT NULL, ApprovedBy nvarchar(max) NOT NULL, ApprovedAt datetimeoffset NOT NULL, ExpiresAt datetimeoffset NOT NULL);
CREATE TABLE dbo.ExportJobs (ExportJobId uniqueidentifier NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, Format int NOT NULL, Status int NOT NULL, CreatedAt datetimeoffset NOT NULL, RequestedBy nvarchar(max) NOT NULL, DownloadUrl nvarchar(max) NULL, FailureReason nvarchar(max) NULL);
CREATE TABLE dbo.RemediationProposals (ProposalId uniqueidentifier NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, FindingId uniqueidentifier NOT NULL, SnapshotId uniqueidentifier NOT NULL, Script nvarchar(max) NOT NULL, ProposedBy nvarchar(max) NOT NULL, ProposedAt datetimeoffset NOT NULL, EvidenceCapturedAt datetimeoffset NOT NULL, EvidenceValidUntil datetimeoffset NOT NULL, Kind int NOT NULL, Status int NOT NULL, ReviewedBy nvarchar(max) NULL, ReviewedAt datetimeoffset NULL, ReviewReason nvarchar(max) NULL);
CREATE TABLE dbo.SnapshotJobs (JobId uniqueidentifier NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, SnapshotId uniqueidentifier NOT NULL, ConnectionId uniqueidentifier NOT NULL, EntraTenantId uniqueidentifier NOT NULL, Mode int NOT NULL, ActorTenantId uniqueidentifier NOT NULL, ActorObjectId uniqueidentifier NOT NULL, SectionsJson nvarchar(4000) NULL, Status int NOT NULL, CreatedAt datetimeoffset NOT NULL, UpdatedAt datetimeoffset NULL, FailureReason nvarchar(2000) NULL);
CREATE TABLE dbo.CustomerLegalHolds (CustomerId uniqueidentifier NOT NULL PRIMARY KEY, Reason nvarchar(1000) NOT NULL, PlacedAt datetimeoffset NOT NULL, ReleasedAt datetimeoffset NULL);
CREATE TABLE dbo.OnboardingReplayNonces (Nonce nvarchar(200) NOT NULL PRIMARY KEY, ExpiresAt datetimeoffset NOT NULL, ConsumedAt datetimeoffset NOT NULL);
CREATE INDEX IX_OnboardingReplayNonces_ExpiresAt ON dbo.OnboardingReplayNonces(ExpiresAt);
CREATE TABLE dbo.AuditEvents (AuditId bigint IDENTITY NOT NULL PRIMARY KEY, CustomerId uniqueidentifier NOT NULL, ActorTenantId uniqueidentifier NOT NULL, ActorObjectId uniqueidentifier NOT NULL, Action nvarchar(300) NOT NULL, TargetType nvarchar(max) NULL, TargetId nvarchar(max) NULL, Timestamp datetimeoffset NOT NULL, Outcome nvarchar(32) NOT NULL, StatusCode int NOT NULL, IpAddress nvarchar(max) NULL, Details nvarchar(max) NULL, CorrelationId nvarchar(max) NOT NULL);
CREATE INDEX IX_AuditEvents_Timestamp ON dbo.AuditEvents(CustomerId, Timestamp);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException("The durable security baseline is expanded and rolled forward; destructive rollback is not supported.");
    }
}