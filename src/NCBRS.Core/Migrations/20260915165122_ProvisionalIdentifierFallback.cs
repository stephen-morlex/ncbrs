using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class ProvisionalIdentifierFallback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProvisionalIdentifier",
                table: "BirthRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReconciledAtUtc",
                table: "BirthRecords",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProvisionalIdentifier",
                table: "BirthRecords");

            migrationBuilder.DropColumn(
                name: "ReconciledAtUtc",
                table: "BirthRecords");
        }
    }
}
