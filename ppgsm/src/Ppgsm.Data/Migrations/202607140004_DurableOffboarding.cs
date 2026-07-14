using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ppgsm.Data.Migrations;

[DbContext(typeof(PpgsmDbContext))]
[Migration("202607140004_DurableOffboarding")]
public sealed class DurableOffboarding : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE dbo.CustomerDeletions ADD
    JobId uniqueidentifier NULL,
    RequestedBy nvarchar(200) NULL,
    ApprovedBy nvarchar(200) NULL,
    ApprovedAt datetimeoffset NULL,
    RetentionExpiresAt datetimeoffset NULL,
    StartedAt datetimeoffset NULL,
    BeforeCountsJson nvarchar(max) NULL,
    AfterCountsJson nvarchar(max) NULL,
    ConsentRevocationReference nvarchar(500) NULL,
    PhysicalDeletionReference nvarchar(500) NULL;
UPDATE dbo.CustomerDeletions
SET JobId = NEWID(), RequestedBy = N'legacy-migration', RetentionExpiresAt = RequestedAt
WHERE JobId IS NULL;
ALTER TABLE dbo.CustomerDeletions ALTER COLUMN JobId uniqueidentifier NOT NULL;
ALTER TABLE dbo.CustomerDeletions ALTER COLUMN RequestedBy nvarchar(200) NOT NULL;
ALTER TABLE dbo.CustomerDeletions ALTER COLUMN RetentionExpiresAt datetimeoffset NOT NULL;
CREATE UNIQUE INDEX IX_CustomerDeletions_JobId ON dbo.CustomerDeletions(JobId);
CREATE INDEX IX_CustomerDeletions_StatusRetention ON dbo.CustomerDeletions(Status, RetentionExpiresAt);
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Durable offboarding metadata is rolled forward; destructive rollback is not supported.");
}