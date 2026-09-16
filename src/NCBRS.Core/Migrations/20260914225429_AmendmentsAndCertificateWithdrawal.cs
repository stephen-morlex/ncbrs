using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class AmendmentsAndCertificateWithdrawal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Certificates_BirthRecords_BirthRecordId",
                table: "Certificates");

            migrationBuilder.DropIndex(
                name: "IX_Certificates_BirthRecordId",
                table: "Certificates");

            migrationBuilder.AddColumn<DateTime>(
                name: "WithdrawnAtUtc",
                table: "Certificates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WithdrawnReason",
                table: "Certificates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BirthRecordAmendments",
                columns: table => new
                {
                    BirthRecordAmendmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Field = table.Column<string>(type: "TEXT", nullable: false),
                    PreviousValue = table.Column<string>(type: "TEXT", nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    AmendedByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmendedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BirthRecordAmendments", x => x.BirthRecordAmendmentId);
                    table.ForeignKey(
                        name: "FK_BirthRecordAmendments_BirthRecords_BirthRecordId",
                        column: x => x.BirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BirthRecordAmendments_Registrars_AmendedByRegistrarId",
                        column: x => x.AmendedByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Certificates_BirthRecordId",
                table: "Certificates",
                column: "BirthRecordId",
                unique: true,
                filter: "\"WithdrawnAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecordAmendments_AmendedByRegistrarId",
                table: "BirthRecordAmendments",
                column: "AmendedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecordAmendments_BirthRecordId_AmendedAtUtc",
                table: "BirthRecordAmendments",
                columns: new[] { "BirthRecordId", "AmendedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecordAmendments_TransactionId",
                table: "BirthRecordAmendments",
                column: "TransactionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Certificates_BirthRecords_BirthRecordId",
                table: "Certificates",
                column: "BirthRecordId",
                principalTable: "BirthRecords",
                principalColumn: "BirthRecordId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Certificates_BirthRecords_BirthRecordId",
                table: "Certificates");

            migrationBuilder.DropTable(
                name: "BirthRecordAmendments");

            migrationBuilder.DropIndex(
                name: "IX_Certificates_BirthRecordId",
                table: "Certificates");

            migrationBuilder.DropColumn(
                name: "WithdrawnAtUtc",
                table: "Certificates");

            migrationBuilder.DropColumn(
                name: "WithdrawnReason",
                table: "Certificates");

            migrationBuilder.CreateIndex(
                name: "IX_Certificates_BirthRecordId",
                table: "Certificates",
                column: "BirthRecordId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Certificates_BirthRecords_BirthRecordId",
                table: "Certificates",
                column: "BirthRecordId",
                principalTable: "BirthRecords",
                principalColumn: "BirthRecordId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
