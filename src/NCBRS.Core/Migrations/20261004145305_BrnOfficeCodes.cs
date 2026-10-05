using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class BrnOfficeCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OfficeCode",
                table: "Facilities",
                type: "TEXT",
                maxLength: 6,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FacilityBrnSequences",
                columns: table => new
                {
                    FacilityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAvailable = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FacilityBrnSequences", x => new { x.FacilityId, x.Year });
                    table.ForeignKey(
                        name: "FK_FacilityBrnSequences_Facilities_FacilityId",
                        column: x => x.FacilityId,
                        principalTable: "Facilities",
                        principalColumn: "FacilityId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Facilities_OfficeCode",
                table: "Facilities",
                column: "OfficeCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FacilityBrnSequences");

            migrationBuilder.DropIndex(
                name: "IX_Facilities_OfficeCode",
                table: "Facilities");

            migrationBuilder.DropColumn(
                name: "OfficeCode",
                table: "Facilities");
        }
    }
}
