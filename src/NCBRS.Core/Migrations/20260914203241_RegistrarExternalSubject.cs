using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class RegistrarExternalSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalSubjectId",
                table: "Registrars",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registrars_ExternalSubjectId",
                table: "Registrars",
                column: "ExternalSubjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Registrars_ExternalSubjectId",
                table: "Registrars");

            migrationBuilder.DropColumn(
                name: "ExternalSubjectId",
                table: "Registrars");
        }
    }
}
