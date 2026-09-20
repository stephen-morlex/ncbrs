using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class AdministrativeAreas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdministrativeAreas",
                columns: table => new
                {
                    AdministrativeAreaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Code = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdministrativeAreas", x => x.AdministrativeAreaId);
                    table.ForeignKey(
                        name: "FK_AdministrativeAreas_AdministrativeAreas_ParentId",
                        column: x => x.ParentId,
                        principalTable: "AdministrativeAreas",
                        principalColumn: "AdministrativeAreaId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdministrativeAreas_Code",
                table: "AdministrativeAreas",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdministrativeAreas_ParentId",
                table: "AdministrativeAreas",
                column: "ParentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdministrativeAreas");
        }
    }
}
