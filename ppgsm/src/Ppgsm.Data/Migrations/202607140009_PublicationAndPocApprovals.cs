using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ppgsm.Data.Migrations;

[DbContext(typeof(PpgsmDbContext))]
[Migration("202607140009_PublicationAndPocApprovals")]
public sealed class PublicationAndPocApprovals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE dbo.Findings ADD
    PublicationContentDigest nvarchar(80) NOT NULL CONSTRAINT DF_Findings_PublicationContentDigest DEFAULT N'legacy',
    EvaluatorVersionsJson nvarchar(max) NOT NULL CONSTRAINT DF_Findings_EvaluatorVersionsJson DEFAULT N'{}';

CREATE TABLE dbo.PocApprovals (
    PocApprovalId uniqueidentifier NOT NULL PRIMARY KEY,
    CustomerId uniqueidentifier NOT NULL,
    RuleId nvarchar(100) NOT NULL,
    Identity nvarchar(200) NOT NULL,
    ApiVersion nvarchar(100) NOT NULL,
    EvidenceReferenceId uniqueidentifier NOT NULL,
    ApprovedBy nvarchar(200) NOT NULL,
    ApprovedAt datetimeoffset NOT NULL,
    ExpiresAt datetimeoffset NOT NULL,
    CONSTRAINT FK_PocApprovals_RawEvidenceReferences FOREIGN KEY (EvidenceReferenceId) REFERENCES dbo.RawEvidenceReferences(RawEvidenceReferenceId),
    CONSTRAINT CK_PocApprovals_Expiry CHECK (ExpiresAt > ApprovedAt));
CREATE INDEX IX_PocApprovals_Scope ON dbo.PocApprovals(CustomerId, RuleId, Identity, ApiVersion, ExpiresAt);

ALTER SECURITY POLICY dbo.PpgsmTenantIsolationPolicy ADD
    FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.PocApprovals,
    BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.PocApprovals AFTER INSERT,
    BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.PocApprovals AFTER UPDATE;
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Publication provenance and PoC approvals are rolled forward; destructive rollback is not supported.");
}