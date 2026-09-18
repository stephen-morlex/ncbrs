using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class FacilityAdministrativeArea : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AdministrativeAreaId",
                table: "Facilities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Facilities_AdministrativeAreaId",
                table: "Facilities",
                column: "AdministrativeAreaId");

            migrationBuilder.AddForeignKey(
                name: "FK_Facilities_AdministrativeAreas_AdministrativeAreaId",
                table: "Facilities",
                column: "AdministrativeAreaId",
                principalTable: "AdministrativeAreas",
                principalColumn: "AdministrativeAreaId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Facilities_AdministrativeAreas_AdministrativeAreaId",
                table: "Facilities");

            migrationBuilder.DropIndex(
                name: "IX_Facilities_AdministrativeAreaId",
                table: "Facilities");

            migrationBuilder.DropColumn(
                name: "AdministrativeAreaId",
                table: "Facilities");
        }
    }
}
