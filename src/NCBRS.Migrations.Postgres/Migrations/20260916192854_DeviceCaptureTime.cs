using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class DeviceCaptureTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RegisteredAtUtc",
                table: "BirthRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StatutoryWindowDays",
                table: "BirthRecords",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RegisteredAtUtc",
                table: "BirthRecords");

            migrationBuilder.DropColumn(
                name: "StatutoryWindowDays",
                table: "BirthRecords");
        }
    }
}
