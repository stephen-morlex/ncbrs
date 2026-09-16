using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class AmendmentConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AmendmentConflicts",
                columns: table => new
                {
                    AmendmentConflictId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmendmentRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Field = table.Column<string>(type: "TEXT", nullable: false),
                    ExpectedPreviousValue = table.Column<string>(type: "TEXT", nullable: true),
                    ActualPreviousValue = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedValue = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    DetectedFromRegistrarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReviewedByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReviewNote = table.Column<string>(type: "TEXT", nullable: true),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmendmentConflicts", x => x.AmendmentConflictId);
                    table.ForeignKey(
                        name: "FK_AmendmentConflicts_BirthRecords_BirthRecordId",
                        column: x => x.BirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AmendmentConflicts_Registrars_ReviewedByRegistrarId",
                        column: x => x.ReviewedByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AmendmentConflicts_BirthRecordId",
                table: "AmendmentConflicts",
                column: "BirthRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_AmendmentConflicts_ReviewedByRegistrarId",
                table: "AmendmentConflicts",
                column: "ReviewedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_AmendmentConflicts_Status",
                table: "AmendmentConflicts",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AmendmentConflicts");
        }
    }
}
