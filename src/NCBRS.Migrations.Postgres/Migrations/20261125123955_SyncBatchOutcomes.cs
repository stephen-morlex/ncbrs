using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class SyncBatchOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DuplicateCount",
                table: "SyncBatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RegisteredCount",
                table: "SyncBatches",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RejectedCount",
                table: "SyncBatches",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DuplicateCount",
                table: "SyncBatches");

            migrationBuilder.DropColumn(
                name: "RegisteredCount",
                table: "SyncBatches");

            migrationBuilder.DropColumn(
                name: "RejectedCount",
                table: "SyncBatches");
        }
    }
}
