using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ppgsm.Data.Migrations;

[DbContext(typeof(PpgsmDbContext))]
[Migration("202607140007_EvidenceProjectionAndJsonExports")]
public sealed class EvidenceProjectionAndJsonExports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
CREATE TABLE dbo.DlpPolicyEvidence (
    DlpPolicyEvidenceId uniqueidentifier NOT NULL PRIMARY KEY,
    CustomerId uniqueidentifier NOT NULL,
    SnapshotId uniqueidentifier NOT NULL,
    PolicyId nvarchar(450) NOT NULL,
    DisplayName nvarchar(500) NOT NULL,
    Properties nvarchar(max) NOT NULL,
    RawEvidenceReferenceId uniqueidentifier NOT NULL
);
CREATE UNIQUE INDEX IX_DlpPolicyEvidence_CustomerId_SnapshotId_PolicyId
    ON dbo.DlpPolicyEvidence(CustomerId, SnapshotId, PolicyId);

ALTER TABLE dbo.ExportJobs ADD
    SnapshotId uniqueidentifier NOT NULL CONSTRAINT DF_ExportJobs_SnapshotId DEFAULT '00000000-0000-0000-0000-000000000000',
    IncludesPii bit NOT NULL CONSTRAINT DF_ExportJobs_IncludesPii DEFAULT 0,
    UpdatedAt datetimeoffset NULL,
    DownloadExpiresAt datetimeoffset NULL;
CREATE INDEX IX_ExportJobs_Status_CreatedAt ON dbo.ExportJobs(Status, CreatedAt);

DROP SECURITY POLICY dbo.PpgsmTenantIsolationPolicy;
CREATE SECURITY POLICY dbo.PpgsmTenantIsolationPolicy
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.Customers,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.Customers AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.Customers AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.TenantMemberships,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.TenantMemberships AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.TenantMemberships AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.TenantConnections,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.TenantConnections AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.TenantConnections AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.TenantCapabilities,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.TenantCapabilities AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.TenantCapabilities AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.Snapshots,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.Snapshots AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.Snapshots AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.SnapshotEnvironmentScopes,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.SnapshotEnvironmentScopes AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.SnapshotEnvironmentScopes AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.SnapshotSections,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.SnapshotSections AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.SnapshotSections AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.RawEvidenceReferences,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.RawEvidenceReferences AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.RawEvidenceReferences AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.CollectorCheckpoints,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.CollectorCheckpoints AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.CollectorCheckpoints AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.CollectorProgress,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.CollectorProgress AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.CollectorProgress AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.TenantSettingsEvidence,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.TenantSettingsEvidence AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.TenantSettingsEvidence AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.EnvironmentEvidence,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.EnvironmentEvidence AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.EnvironmentEvidence AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.DlpPolicyEvidence,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.DlpPolicyEvidence AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.DlpPolicyEvidence AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.Findings,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.Findings AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.Findings AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.GovernanceExceptions,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.GovernanceExceptions AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.GovernanceExceptions AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.ExportJobs,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.ExportJobs AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.ExportJobs AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.RemediationProposals,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.RemediationProposals AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.RemediationProposals AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.SnapshotJobs,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.SnapshotJobs AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.SnapshotJobs AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.CustomerLegalHolds,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.CustomerLegalHolds AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.CustomerLegalHolds AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.CustomerDeletions,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.CustomerDeletions AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.CustomerDeletions AFTER UPDATE,
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.AuditEvents,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.AuditEvents AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.AuditEvents AFTER UPDATE
WITH (STATE = ON);
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Evidence projection and export lifecycle changes are rolled forward; destructive rollback is not supported.");
}
