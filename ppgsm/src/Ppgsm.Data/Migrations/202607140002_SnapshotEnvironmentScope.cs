using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ppgsm.Data.Migrations;

[DbContext(typeof(PpgsmDbContext))]
[Migration("2026071400051_SnapshotEnvironmentScope")]
public sealed class SnapshotEnvironmentScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "EnvironmentIdsJson",
            table: "SnapshotJobs",
            type: "nvarchar(4000)",
            maxLength: 4000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name: "EnvironmentIdsJson", table: "SnapshotJobs");
}