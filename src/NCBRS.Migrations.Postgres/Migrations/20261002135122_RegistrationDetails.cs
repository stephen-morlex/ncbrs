using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class RegistrationDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "People",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GivenNames",
                table: "People",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityDocumentNumber",
                table: "People",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityDocumentType",
                table: "People",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaidenSurname",
                table: "People",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Occupation",
                table: "People",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfBirth",
                table: "People",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Surname",
                table: "People",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarriageCertificateNumber",
                table: "BirthRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ParentsMarriageDate",
                table: "BirthRecords",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfBirth",
                table: "BirthRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaceOfBirthKind",
                table: "BirthRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProofOfAddressKind",
                table: "BirthRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProofOfAddressReference",
                table: "BirthRecords",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Address",
                table: "People");

            migrationBuilder.DropColumn(
                name: "GivenNames",
                table: "People");

            migrationBuilder.DropColumn(
                name: "IdentityDocumentNumber",
                table: "People");

            migrationBuilder.DropColumn(
                name: "IdentityDocumentType",
                table: "People");

            migrationBuilder.DropColumn(
                name: "MaidenSurname",
                table: "People");

            migrationBuilder.DropColumn(
                name: "Occupation",
                table: "People");

            migrationBuilder.DropColumn(
                name: "PlaceOfBirth",
                table: "People");

            migrationBuilder.DropColumn(
                name: "Surname",
                table: "People");

            migrationBuilder.DropColumn(
                name: "MarriageCertificateNumber",
                table: "BirthRecords");

            migrationBuilder.DropColumn(
                name: "ParentsMarriageDate",
                table: "BirthRecords");

            migrationBuilder.DropColumn(
                name: "PlaceOfBirth",
                table: "BirthRecords");

            migrationBuilder.DropColumn(
                name: "PlaceOfBirthKind",
                table: "BirthRecords");

            migrationBuilder.DropColumn(
                name: "ProofOfAddressKind",
                table: "BirthRecords");

            migrationBuilder.DropColumn(
                name: "ProofOfAddressReference",
                table: "BirthRecords");
        }
    }
}
