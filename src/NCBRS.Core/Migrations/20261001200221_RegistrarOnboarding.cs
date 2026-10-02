using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WithdrawnByRegistrarId",
                table: "Registrars",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WithdrawnReason",
                table: "Registrars",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PendingAccounts",
                columns: table => new
                {
                    PendingAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    RealmRoles = table.Column<string>(type: "TEXT", nullable: false),
                    CountyCode = table.Column<string>(type: "TEXT", nullable: true),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
