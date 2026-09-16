using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class LateRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LateRegistrations",
                columns: table => new
                {
                    LateRegistrationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DaysLate = table.Column<int>(type: "INTEGER", nullable: false),
                    WindowDaysAtFiling = table.Column<int>(type: "INTEGER", nullable: false),
                    EvidenceType = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceReference = table.Column<string>(type: "TEXT", nullable: true),
                    DeclarantName = table.Column<string>(type: "TEXT", nullable: false),
                    DeclarantRelationship = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    SubmittedByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReviewedByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReviewNote = table.Column<string>(type: "TEXT", nullable: true),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LateRegistrations", x => x.LateRegistrationId);
                    table.ForeignKey(
                        name: "FK_LateRegistrations_BirthRecords_BirthRecordId",
                        column: x => x.BirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LateRegistrations_Registrars_ReviewedByRegistrarId",
                        column: x => x.ReviewedByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LateRegistrations_Registrars_SubmittedByRegistrarId",
                        column: x => x.SubmittedByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LateRegistrations_BirthRecordId",
                table: "LateRegistrations",
                column: "BirthRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LateRegistrations_ReviewedByRegistrarId",
                table: "LateRegistrations",
                column: "ReviewedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_LateRegistrations_Status",
                table: "LateRegistrations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_LateRegistrations_SubmittedByRegistrarId",
                table: "LateRegistrations",
                column: "SubmittedByRegistrarId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LateRegistrations");
        }
    }
}
