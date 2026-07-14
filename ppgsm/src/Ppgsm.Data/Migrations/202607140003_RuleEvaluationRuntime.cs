using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ppgsm.Data.Migrations;

[DbContext(typeof(PpgsmDbContext))]
[Migration("2026071400052_RuleEvaluationRuntime")]
public sealed class RuleEvaluationRuntime : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>("RuleVersion", "Findings", type: "int", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<string>("CatalogVersion", "Findings", type: "nvarchar(40)", maxLength: 40, nullable: false, defaultValue: "legacy");
        migrationBuilder.AddColumn<string>("EvaluatorKey", "Findings", type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: "legacy");
        migrationBuilder.AddColumn<int>("EvaluatorVersion", "Findings", type: "int", nullable: false, defaultValue: 1);
        migrationBuilder.AddColumn<string>("EvidenceLinksJson", "Findings", type: "nvarchar(max)", nullable: false, defaultValue: "[]");
        migrationBuilder.CreateTable(
            name: "SnapshotEnvironmentScopes",
            columns: table => new
            {
                SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RequestedEnvironmentIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                DiscoveredEnvironmentIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                DiscoveryAuthoritative = table.Column<bool>(type: "bit", nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_SnapshotEnvironmentScopes", value => value.SnapshotId));
        migrationBuilder.CreateIndex("IX_SnapshotEnvironmentScopes_Customer", "SnapshotEnvironmentScopes", new[] { "CustomerId", "SnapshotId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("SnapshotEnvironmentScopes");
        migrationBuilder.DropColumn("RuleVersion", "Findings");
        migrationBuilder.DropColumn("CatalogVersion", "Findings");
        migrationBuilder.DropColumn("EvaluatorKey", "Findings");
        migrationBuilder.DropColumn("EvaluatorVersion", "Findings");
        migrationBuilder.DropColumn("EvidenceLinksJson", "Findings");
    }
}