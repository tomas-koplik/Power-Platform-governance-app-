using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ppgsm.Data.Migrations;

[DbContext(typeof(PpgsmDbContext))]
[Migration("202607140008_ExportArtifactIntegrity")]
public sealed class ExportArtifactIntegrity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER TABLE dbo.ExportJobs ADD
    ArtifactContentHash nvarchar(80) NULL,
    ArtifactContentLength bigint NULL,
    ArtifactMediaType nvarchar(100) NULL,
    ArtifactStorageETag nvarchar(200) NULL;
""");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Export integrity metadata is rolled forward; destructive rollback is not supported.");
}