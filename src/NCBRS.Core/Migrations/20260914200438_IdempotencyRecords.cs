using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class IdempotencyRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_TransactionId",
                table: "RequestLogs");

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    IdempotencyRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    ResponseBody = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.IdempotencyRecordId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_TransactionId",
                table: "RequestLogs",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_CompletedAtUtc",
                table: "IdempotencyRecords",
                column: "CompletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_TransactionId",
                table: "IdempotencyRecords",
                column: "TransactionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdempotencyRecords");

            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_TransactionId",
                table: "RequestLogs");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_TransactionId",
                table: "RequestLogs",
                column: "TransactionId",
                unique: true);
        }
    }
}
