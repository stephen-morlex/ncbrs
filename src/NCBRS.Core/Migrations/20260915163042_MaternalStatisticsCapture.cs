using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class MaternalStatisticsCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FatherEducationLevelYears",
                table: "MaternalStatistics");

            migrationBuilder.AddColumn<string>(
                name: "FatherEducationLevel",
                table: "MaternalStatistics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecordedAtUtc",
                table: "MaternalStatistics",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "RecordedByRegistrarId",
                table: "MaternalStatistics",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TransactionId",
                table: "MaternalStatistics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtUtc",
                table: "MaternalStatistics",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaternalStatistics_RecordedByRegistrarId",
                table: "MaternalStatistics",
                column: "RecordedByRegistrarId");

            migrationBuilder.AddForeignKey(
                name: "FK_MaternalStatistics_Registrars_RecordedByRegistrarId",
                table: "MaternalStatistics",
                column: "RecordedByRegistrarId",
                principalTable: "Registrars",
                principalColumn: "RegistrarId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaternalStatistics_Registrars_RecordedByRegistrarId",
                table: "MaternalStatistics");

            migrationBuilder.DropIndex(
                name: "IX_MaternalStatistics_RecordedByRegistrarId",
                table: "MaternalStatistics");

            migrationBuilder.DropColumn(
                name: "FatherEducationLevel",
                table: "MaternalStatistics");

            migrationBuilder.DropColumn(
                name: "RecordedAtUtc",
                table: "MaternalStatistics");

            migrationBuilder.DropColumn(
                name: "RecordedByRegistrarId",
                table: "MaternalStatistics");

            migrationBuilder.DropColumn(
                name: "TransactionId",
                table: "MaternalStatistics");

            migrationBuilder.DropColumn(
                name: "UpdatedAtUtc",
                table: "MaternalStatistics");

            migrationBuilder.AddColumn<int>(
                name: "FatherEducationLevelYears",
                table: "MaternalStatistics",
                type: "INTEGER",
                nullable: true);
        }
    }
}
