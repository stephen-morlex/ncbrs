using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class UniqueTransactionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_TransactionId",
                table: "RequestLogs");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_TransactionId",
                table: "RequestLogs",
                column: "TransactionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RequestLogs_TransactionId",
                table: "RequestLogs");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_TransactionId",
                table: "RequestLogs",
                column: "TransactionId");
        }
    }
}
