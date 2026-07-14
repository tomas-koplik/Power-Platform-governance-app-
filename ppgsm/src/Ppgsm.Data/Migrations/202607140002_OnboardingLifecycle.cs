using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ppgsm.Data.Migrations;

[DbContext(typeof(PpgsmDbContext))]
[Migration("202607140002_OnboardingLifecycle")]
public sealed class OnboardingLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE dbo.TenantConnections ADD CONSTRAINT UQ_TenantConnections_Customer UNIQUE (CustomerId);
UPDATE dbo.TenantConnections SET Status = CASE Status WHEN 0 THEN 1 WHEN 1 THEN 3 ELSE Status END;
CREATE TABLE dbo.TenantCapabilities (
    TenantCapabilityId uniqueidentifier NOT NULL PRIMARY KEY,
    CustomerId uniqueidentifier NOT NULL,
    ConnectionId uniqueidentifier NOT NULL,
    Endpoint nvarchar(500) NOT NULL,
    Identity nvarchar(200) NOT NULL,
    Available bit NOT NULL,
    Detail nvarchar(1000) NOT NULL,
    VerifiedAt datetimeoffset NOT NULL);
CREATE UNIQUE INDEX IX_TenantCapabilities_Endpoint ON dbo.TenantCapabilities(CustomerId, ConnectionId, Endpoint, Identity);
CREATE TABLE dbo.CustomerDeletions (
    CustomerId uniqueidentifier NOT NULL PRIMARY KEY,
    Status int NOT NULL,
    RequestedAt datetimeoffset NOT NULL,
    CompletedAt datetimeoffset NULL,
    Detail nvarchar(2000) NULL,
    CertificateId nvarchar(100) NULL);
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("The onboarding lifecycle migration is rolled forward; destructive rollback is not supported.");
}
