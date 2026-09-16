using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class DuplicateCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByBirthRecordId",
                table: "BirthRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DuplicateCandidates",
                columns: table => new
                {
                    DuplicateCandidateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MatchedBirthRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    Reasons = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReviewedByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReviewNote = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateCandidates", x => x.DuplicateCandidateId);
                    table.ForeignKey(
                        name: "FK_DuplicateCandidates_BirthRecords_BirthRecordId",
                        column: x => x.BirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DuplicateCandidates_BirthRecords_MatchedBirthRecordId",
                        column: x => x.MatchedBirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DuplicateCandidates_Registrars_ReviewedByRegistrarId",
                        column: x => x.ReviewedByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecords_DateOfBirth",
                table: "BirthRecords",
                column: "DateOfBirth");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateCandidates_BirthRecordId",
                table: "DuplicateCandidates",
                column: "BirthRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateCandidates_MatchedBirthRecordId",
                table: "DuplicateCandidates",
                column: "MatchedBirthRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateCandidates_ReviewedByRegistrarId",
                table: "DuplicateCandidates",
                column: "ReviewedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateCandidates_Status",
                table: "DuplicateCandidates",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DuplicateCandidates");

            migrationBuilder.DropIndex(
                name: "IX_BirthRecords_DateOfBirth",
                table: "BirthRecords");

            migrationBuilder.DropColumn(
                name: "SupersededByBirthRecordId",
                table: "BirthRecords");
        }
    }
}
