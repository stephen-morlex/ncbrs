using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class TwoTrackAmendmentApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AmendmentRequestId",
                table: "BirthRecordAmendments",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "AppliedAtUtc",
                table: "BirthRecordAmendments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "BirthRecordAmendments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedAtUtc",
                table: "BirthRecordAmendments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedByRegistrarId",
                table: "BirthRecordAmendments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "BirthRecordAmendments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Every amendment recorded before the approval track existed was
            // applied the moment it was submitted. Without this backfill they
            // read back with an empty Status -- not a valid enum value -- and
            // a request id of all zeros, which would group unrelated historic
            // corrections into one apparently reviewable request.
            //
            // TransactionId is the right grouping key: rows written by one
            // submission shared it. The row's own id is the fallback for any
            // amendment recorded without one.
            migrationBuilder.Sql(
                """
                UPDATE "BirthRecordAmendments"
                SET "Status" = 'Applied',
                    "AppliedAtUtc" = "AmendedAtUtc",
                    "AmendmentRequestId" = COALESCE("TransactionId", "BirthRecordAmendmentId")
                WHERE "Status" = '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecordAmendments_AmendmentRequestId",
                table: "BirthRecordAmendments",
                column: "AmendmentRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecordAmendments_ReviewedByRegistrarId",
                table: "BirthRecordAmendments",
                column: "ReviewedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecordAmendments_Status",
                table: "BirthRecordAmendments",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_BirthRecordAmendments_Registrars_ReviewedByRegistrarId",
                table: "BirthRecordAmendments",
                column: "ReviewedByRegistrarId",
                principalTable: "Registrars",
                principalColumn: "RegistrarId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BirthRecordAmendments_Registrars_ReviewedByRegistrarId",
                table: "BirthRecordAmendments");

            migrationBuilder.DropIndex(
                name: "IX_BirthRecordAmendments_AmendmentRequestId",
                table: "BirthRecordAmendments");

            migrationBuilder.DropIndex(
                name: "IX_BirthRecordAmendments_ReviewedByRegistrarId",
                table: "BirthRecordAmendments");

            migrationBuilder.DropIndex(
                name: "IX_BirthRecordAmendments_Status",
                table: "BirthRecordAmendments");

            migrationBuilder.DropColumn(
                name: "AmendmentRequestId",
                table: "BirthRecordAmendments");

            migrationBuilder.DropColumn(
                name: "AppliedAtUtc",
                table: "BirthRecordAmendments");

            migrationBuilder.DropColumn(
                name: "ReviewNote",
                table: "BirthRecordAmendments");

            migrationBuilder.DropColumn(
                name: "ReviewedAtUtc",
                table: "BirthRecordAmendments");

            migrationBuilder.DropColumn(
                name: "ReviewedByRegistrarId",
                table: "BirthRecordAmendments");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "BirthRecordAmendments");
        }
    }
}
