using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class RegistrarOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "WithdrawnAtUtc",
                table: "Registrars",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WithdrawnByRegistrarId",
                table: "Registrars",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WithdrawnReason",
                table: "Registrars",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PendingAccounts",
                columns: table => new
                {
                    PendingAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: true),
                    Email = table.Column<string>(type: "text", nullable: true),
                    RealmRoles = table.Column<string>(type: "text", nullable: false),
                    CountyCode = table.Column<string>(type: "text", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAccounts", x => x.PendingAccountId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingAccounts_CountyCode",
                table: "PendingAccounts",
                column: "CountyCode");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAccounts_Subject",
                table: "PendingAccounts",
                column: "Subject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingAccounts");

            migrationBuilder.DropColumn(
                name: "WithdrawnAtUtc",
                table: "Registrars");

            migrationBuilder.DropColumn(
                name: "WithdrawnByRegistrarId",
                table: "Registrars");

            migrationBuilder.DropColumn(
                name: "WithdrawnReason",
                table: "Registrars");
        }
    }
}
