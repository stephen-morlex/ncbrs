using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class RecordAnnulment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AnnulledAtUtc",
                table: "BirthRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RecordAnnulments",
                columns: table => new
                {
                    RecordAnnulmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    Justification = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorityReference = table.Column<string>(type: "TEXT", nullable: true),
                    AnnulledByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnnulledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CertificateRevoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordAnnulments", x => x.RecordAnnulmentId);
                    table.ForeignKey(
                        name: "FK_RecordAnnulments_BirthRecords_BirthRecordId",
                        column: x => x.BirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecordAnnulments_Registrars_AnnulledByRegistrarId",
                        column: x => x.AnnulledByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecords_AnnulledAtUtc",
                table: "BirthRecords",
                column: "AnnulledAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RecordAnnulments_AnnulledByRegistrarId",
                table: "RecordAnnulments",
                column: "AnnulledByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_RecordAnnulments_BirthRecordId",
                table: "RecordAnnulments",
                column: "BirthRecordId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecordAnnulments");

            migrationBuilder.DropIndex(
                name: "IX_BirthRecords_AnnulledAtUtc",
                table: "BirthRecords");

            migrationBuilder.DropColumn(
                name: "AnnulledAtUtc",
                table: "BirthRecords");
        }
    }
}
