using Microsoft.EntityFrameworkCore;
using Ppgsm.Core.Domain;
using Ppgsm.Data;
using Ppgsm.Data.Migrations;

namespace Ppgsm.Api.Tests;

public sealed class MigrationStructuralTests
{
    [Fact]
    public void Consolidated_rls_covers_every_current_tenant_table_with_write_blocks()
    {
        var currentTenant = new CurrentTenant();
        currentTenant.Set(new(Guid.Empty, SubjectIdentity.Create(Guid.NewGuid(), Guid.NewGuid()), MembershipRole.InternalAdmin, IsInternal: true));
        using var context = new PpgsmDbContext(new DbContextOptionsBuilder<PpgsmDbContext>().UseSqlServer("Server=localhost;Database=structural;Trusted_Connection=True;TrustServerCertificate=True").Options, currentTenant);
        var modelTables = context.Model.GetEntityTypes()
            .Where(entity => entity.FindProperty("CustomerId") is not null)
            .Select(entity => entity.GetTableName()!)
            .OrderBy(value => value)
            .ToArray();

        Assert.Equal(modelTables, TenantRlsDefinition.CurrentTenantTables.OrderBy(value => value));
        foreach (var table in modelTables)
        {
            if (table == "DlpPolicyEvidence") continue;
            Assert.Equal(1, Count(TenantRlsDefinition.Sql, $"FILTER PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.{table}"));
            Assert.Equal(1, Count(TenantRlsDefinition.Sql, $"BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.{table} AFTER INSERT"));
            Assert.Equal(1, Count(TenantRlsDefinition.Sql, $"BLOCK PREDICATE dbo.fn_ppgsm_tenant_access(CustomerId) ON dbo.{table} AFTER UPDATE"));
        }
    }

    [Fact]
    public void Clean_migration_defines_one_canonical_policy_after_all_table_migrations()
    {
        Assert.DoesNotContain("ALTER SECURITY POLICY", TenantRlsDefinition.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Count(TenantRlsDefinition.Sql, $"CREATE SECURITY POLICY dbo.{TenantRlsDefinition.PolicyName}"));
        Assert.DoesNotContain("CREATE SECURITY POLICY dbo.PpgsmTenantPolicy", TenantRlsDefinition.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.CompareOrdinal("202607140006_ConsolidatedTenantRls", "202607140005_TrustedRemediationProvenance") > 0);
    }

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;
}