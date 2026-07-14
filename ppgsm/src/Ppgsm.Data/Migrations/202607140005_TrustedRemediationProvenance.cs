using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ppgsm.Data.Migrations;

[DbContext(typeof(PpgsmDbContext))]
[Migration("202607140005_TrustedRemediationProvenance")]
public sealed class TrustedRemediationProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE dbo.RemediationProposals ADD
    RuleId nvarchar(100) NOT NULL CONSTRAINT DF_RemediationProposals_RuleId DEFAULT N'legacy',
    RuleVersion int NOT NULL CONSTRAINT DF_RemediationProposals_RuleVersion DEFAULT 0,
    CatalogVersion nvarchar(40) NOT NULL CONSTRAINT DF_RemediationProposals_CatalogVersion DEFAULT N'legacy',
    TemplateId nvarchar(200) NOT NULL CONSTRAINT DF_RemediationProposals_TemplateId DEFAULT N'legacy',
    TemplateVersion int NOT NULL CONSTRAINT DF_RemediationProposals_TemplateVersion DEFAULT 0,
    EvidenceHash nvarchar(128) NOT NULL CONSTRAINT DF_RemediationProposals_EvidenceHash DEFAULT N'legacy',
    ParametersJson nvarchar(max) NOT NULL CONSTRAINT DF_RemediationProposals_ParametersJson DEFAULT N'{}',
    TargetScope nvarchar(500) NOT NULL CONSTRAINT DF_RemediationProposals_TargetScope DEFAULT N'legacy',
    Verification nvarchar(max) NOT NULL CONSTRAINT DF_RemediationProposals_Verification DEFAULT N'legacy',
    Rollback nvarchar(max) NOT NULL CONSTRAINT DF_RemediationProposals_Rollback DEFAULT N'legacy';
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Trusted remediation provenance is rolled forward; destructive rollback is not supported.");
}