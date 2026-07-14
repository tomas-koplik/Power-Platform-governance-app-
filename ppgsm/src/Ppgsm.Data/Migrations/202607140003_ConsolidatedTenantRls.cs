using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ppgsm.Data.Migrations;

[DbContext(typeof(PpgsmDbContext))]
[Migration("202607140006_ConsolidatedTenantRls")]
public sealed class ConsolidatedTenantRls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(TenantRlsDefinition.Sql);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Tenant isolation is rolled forward; destructive rollback is not supported.");
}

public static class TenantRlsDefinition
{
    public const string PolicyName = "PpgsmTenantIsolationPolicy";

    public static readonly IReadOnlyList<string> TenantTables =
    [
        "Customers", "TenantMemberships", "TenantConnections", "TenantCapabilities", "Snapshots", "SnapshotEnvironmentScopes",
        "SnapshotSections", "RawEvidenceReferences", "CollectorCheckpoints", "CollectorProgress",
        "TenantSettingsEvidence", "EnvironmentEvidence", "Findings", "GovernanceExceptions", "ExportJobs",
        "RemediationProposals", "SnapshotJobs", "CustomerLegalHolds", "CustomerDeletions", "AuditEvents"
    ];

    public static IReadOnlyList<string> CurrentTenantTables => [.. TenantTables, "DlpPolicyEvidence"];

    public static string Sql { get; } = BuildSql();

    private static string BuildSql()
    {
        var predicates = string.Join(",\n", TenantTables.Select(table => $"""
ADD FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.{table},
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.{table} AFTER INSERT,
ADD BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.{table} AFTER UPDATE
"""));

        return $"""
IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'PpgsmTenantPolicy' AND schema_id = SCHEMA_ID(N'dbo'))
    DROP SECURITY POLICY dbo.PpgsmTenantPolicy;
IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'{PolicyName}' AND schema_id = SCHEMA_ID(N'dbo'))
    DROP SECURITY POLICY dbo.{PolicyName};

CREATE OR ALTER FUNCTION dbo.fn_ppgsm_tenant_access(@CustomerId uniqueidentifier)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN SELECT 1 AS allowed
WHERE TRY_CAST(SESSION_CONTEXT(N'IsInternal') AS bit) = 1
   OR @CustomerId = TRY_CAST(SESSION_CONTEXT(N'CustomerId') AS uniqueidentifier);

CREATE SECURITY POLICY dbo.{PolicyName}
{predicates}
WITH (STATE = ON);

DENY UPDATE, DELETE ON dbo.AuditEvents TO PUBLIC;
""";
    }
}