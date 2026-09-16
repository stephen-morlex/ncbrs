using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class CertificateRevocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CertificateRevocations",
                columns: table => new
                {
                    CertificateRevocationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CertificateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SerialHash = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CertificateRevocations", x => x.CertificateRevocationId);
                    table.ForeignKey(
                        name: "FK_CertificateRevocations_Certificates_CertificateId",
                        column: x => x.CertificateId,
                        principalTable: "Certificates",
                        principalColumn: "CertificateId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CertificateRevocations_CertificateId",
                table: "CertificateRevocations",
                column: "CertificateId");

            migrationBuilder.CreateIndex(
                name: "IX_CertificateRevocations_RevokedAtUtc",
                table: "CertificateRevocations",
                column: "RevokedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CertificateRevocations_SerialHash",
                table: "CertificateRevocations",
                column: "SerialHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CertificateRevocations");
        }
    }
}
