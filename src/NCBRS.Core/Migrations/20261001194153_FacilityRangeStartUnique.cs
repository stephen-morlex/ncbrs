using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class FacilityRangeStartUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Facilities_BrnBlockStart",
                table: "Facilities",
                column: "BrnBlockStart",
                unique: true,
                filter: "\"BrnBlockEnd\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Facilities_BrnBlockStart",
                table: "Facilities");
        }
    }
}
