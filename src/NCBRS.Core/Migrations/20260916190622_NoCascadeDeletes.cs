using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class NoCascadeDeletes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BirthRecords_Facilities_FacilityId",
                table: "BirthRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_BirthRecords_Registrars_RegisteredByRegistrarId",
                table: "BirthRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_MaternalOutcomes_BirthRecords_BirthRecordId",
                table: "MaternalOutcomes");

            migrationBuilder.DropForeignKey(
                name: "FK_MaternalOutcomes_Registrars_RecordedByRegistrarId",
                table: "MaternalOutcomes");

            migrationBuilder.DropForeignKey(
                name: "FK_MaternalStatistics_BirthRecords_BirthRecordId",
                table: "MaternalStatistics");

            migrationBuilder.DropForeignKey(
                name: "FK_NeonatalOutcomes_BirthRecords_BirthRecordId",
                table: "NeonatalOutcomes");

            migrationBuilder.DropForeignKey(
                name: "FK_NeonatalOutcomes_Registrars_RecordedByRegistrarId",
                table: "NeonatalOutcomes");

            migrationBuilder.DropForeignKey(
                name: "FK_Registrars_Facilities_FacilityId",
                table: "Registrars");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncBatches_Facilities_FacilityId",
                table: "SyncBatches");

            migrationBuilder.AddForeignKey(
                name: "FK_BirthRecords_Facilities_FacilityId",
                table: "BirthRecords",
                column: "FacilityId",
                principalTable: "Facilities",
                principalColumn: "FacilityId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BirthRecords_Registrars_RegisteredByRegistrarId",
                table: "BirthRecords",
                column: "RegisteredByRegistrarId",
                principalTable: "Registrars",
                principalColumn: "RegistrarId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MaternalOutcomes_BirthRecords_BirthRecordId",
                table: "MaternalOutcomes",
                column: "BirthRecordId",
                principalTable: "BirthRecords",
                principalColumn: "BirthRecordId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MaternalOutcomes_Registrars_RecordedByRegistrarId",
                table: "MaternalOutcomes",
                column: "RecordedByRegistrarId",
                principalTable: "Registrars",
                principalColumn: "RegistrarId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MaternalStatistics_BirthRecords_BirthRecordId",
                table: "MaternalStatistics",
                column: "BirthRecordId",
                principalTable: "BirthRecords",
                principalColumn: "BirthRecordId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_NeonatalOutcomes_BirthRecords_BirthRecordId",
                table: "NeonatalOutcomes",
                column: "BirthRecordId",
                principalTable: "BirthRecords",
                principalColumn: "BirthRecordId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_NeonatalOutcomes_Registrars_RecordedByRegistrarId",
                table: "NeonatalOutcomes",
                column: "RecordedByRegistrarId",
                principalTable: "Registrars",
                principalColumn: "RegistrarId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Registrars_Facilities_FacilityId",
                table: "Registrars",
                column: "FacilityId",
                principalTable: "Facilities",
                principalColumn: "FacilityId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncBatches_Facilities_FacilityId",
                table: "SyncBatches",
                column: "FacilityId",
                principalTable: "Facilities",
                principalColumn: "FacilityId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BirthRecords_Facilities_FacilityId",
                table: "BirthRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_BirthRecords_Registrars_RegisteredByRegistrarId",
                table: "BirthRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_MaternalOutcomes_BirthRecords_BirthRecordId",
                table: "MaternalOutcomes");

            migrationBuilder.DropForeignKey(
                name: "FK_MaternalOutcomes_Registrars_RecordedByRegistrarId",
                table: "MaternalOutcomes");

            migrationBuilder.DropForeignKey(
                name: "FK_MaternalStatistics_BirthRecords_BirthRecordId",
                table: "MaternalStatistics");

            migrationBuilder.DropForeignKey(
                name: "FK_NeonatalOutcomes_BirthRecords_BirthRecordId",
                table: "NeonatalOutcomes");

            migrationBuilder.DropForeignKey(
                name: "FK_NeonatalOutcomes_Registrars_RecordedByRegistrarId",
                table: "NeonatalOutcomes");

            migrationBuilder.DropForeignKey(
                name: "FK_Registrars_Facilities_FacilityId",
                table: "Registrars");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncBatches_Facilities_FacilityId",
                table: "SyncBatches");

            migrationBuilder.AddForeignKey(
                name: "FK_BirthRecords_Facilities_FacilityId",
                table: "BirthRecords",
                column: "FacilityId",
                principalTable: "Facilities",
                principalColumn: "FacilityId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_BirthRecords_Registrars_RegisteredByRegistrarId",
                table: "BirthRecords",
                column: "RegisteredByRegistrarId",
                principalTable: "Registrars",
                principalColumn: "RegistrarId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MaternalOutcomes_BirthRecords_BirthRecordId",
                table: "MaternalOutcomes",
                column: "BirthRecordId",
                principalTable: "BirthRecords",
                principalColumn: "BirthRecordId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MaternalOutcomes_Registrars_RecordedByRegistrarId",
                table: "MaternalOutcomes",
                column: "RecordedByRegistrarId",
                principalTable: "Registrars",
                principalColumn: "RegistrarId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MaternalStatistics_BirthRecords_BirthRecordId",
                table: "MaternalStatistics",
                column: "BirthRecordId",
                principalTable: "BirthRecords",
                principalColumn: "BirthRecordId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_NeonatalOutcomes_BirthRecords_BirthRecordId",
                table: "NeonatalOutcomes",
                column: "BirthRecordId",
                principalTable: "BirthRecords",
                principalColumn: "BirthRecordId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_NeonatalOutcomes_Registrars_RecordedByRegistrarId",
                table: "NeonatalOutcomes",
                column: "RecordedByRegistrarId",
                principalTable: "Registrars",
                principalColumn: "RegistrarId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Registrars_Facilities_FacilityId",
                table: "Registrars",
                column: "FacilityId",
                principalTable: "Facilities",
                principalColumn: "FacilityId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncBatches_Facilities_FacilityId",
                table: "SyncBatches",
                column: "FacilityId",
                principalTable: "Facilities",
                principalColumn: "FacilityId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
