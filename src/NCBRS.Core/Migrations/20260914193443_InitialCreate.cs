using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    AuditLogId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.AuditLogId);
                });

            migrationBuilder.CreateTable(
                name: "Facilities",
                columns: table => new
                {
                    FacilityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Tier = table.Column<string>(type: "TEXT", nullable: false),
                    DistrictId = table.Column<string>(type: "TEXT", nullable: false),
                    ConnectivityProfile = table.Column<string>(type: "TEXT", nullable: false),
                    BrnBlockStart = table.Column<long>(type: "INTEGER", nullable: false),
                    BrnBlockEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    BrnBlockNextAvailable = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Facilities", x => x.FacilityId);
                });

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FullName = table.Column<string>(type: "TEXT", nullable: false),
                    DateOfBirth = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    NationalIdRef = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.PersonId);
                });

            migrationBuilder.CreateTable(
                name: "RequestLogs",
                columns: table => new
                {
                    RequestLogId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", nullable: true),
                    TransactionIdGenerated = table.Column<bool>(type: "INTEGER", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogs", x => x.RequestLogId);
                });

            migrationBuilder.CreateTable(
                name: "Registrars",
                columns: table => new
                {
                    RegistrarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FacilityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialHash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Registrars", x => x.RegistrarId);
                    table.ForeignKey(
                        name: "FK_Registrars_Facilities_FacilityId",
                        column: x => x.FacilityId,
                        principalTable: "Facilities",
                        principalColumn: "FacilityId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncBatches",
                columns: table => new
                {
                    SyncBatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    FacilityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncBatches", x => x.SyncBatchId);
                    table.ForeignKey(
                        name: "FK_SyncBatches_Facilities_FacilityId",
                        column: x => x.FacilityId,
                        principalTable: "Facilities",
                        principalColumn: "FacilityId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BirthRecords",
                columns: table => new
                {
                    BirthRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Brn = table.Column<string>(type: "TEXT", nullable: false),
                    VitalEventType = table.Column<string>(type: "TEXT", nullable: false),
                    ChildPersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MotherPersonId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FatherPersonId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FacilityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RegisteredByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DateOfBirth = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Sex = table.Column<string>(type: "TEXT", nullable: false),
                    BirthWeightGrams = table.Column<int>(type: "INTEGER", nullable: true),
                    GestationalAgeWeeks = table.Column<decimal>(type: "TEXT", nullable: true),
                    Plurality = table.Column<string>(type: "TEXT", nullable: false),
                    BirthOrder = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BirthRecords", x => x.BirthRecordId);
                    table.ForeignKey(
                        name: "FK_BirthRecords_Facilities_FacilityId",
                        column: x => x.FacilityId,
                        principalTable: "Facilities",
                        principalColumn: "FacilityId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BirthRecords_People_ChildPersonId",
                        column: x => x.ChildPersonId,
                        principalTable: "People",
                        principalColumn: "PersonId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BirthRecords_People_FatherPersonId",
                        column: x => x.FatherPersonId,
                        principalTable: "People",
                        principalColumn: "PersonId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BirthRecords_People_MotherPersonId",
                        column: x => x.MotherPersonId,
                        principalTable: "People",
                        principalColumn: "PersonId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BirthRecords_Registrars_RegisteredByRegistrarId",
                        column: x => x.RegisteredByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Certificates",
                columns: table => new
                {
                    CertificateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IssueDateUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SignatureHash = table.Column<string>(type: "TEXT", nullable: false),
                    QrPayload = table.Column<string>(type: "TEXT", nullable: false),
                    ReprintCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Certificates", x => x.CertificateId);
                    table.ForeignKey(
                        name: "FK_Certificates_BirthRecords_BirthRecordId",
                        column: x => x.BirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaternalOutcomes",
                columns: table => new
                {
                    BirthRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeathDateUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IcdMmCauseCode = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaternalOutcomes", x => x.BirthRecordId);
                    table.ForeignKey(
                        name: "FK_MaternalOutcomes_BirthRecords_BirthRecordId",
                        column: x => x.BirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaternalOutcomes_Registrars_RecordedByRegistrarId",
                        column: x => x.RecordedByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaternalStatistics",
                columns: table => new
                {
                    BirthRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MotherEducationLevel = table.Column<string>(type: "TEXT", nullable: true),
                    MotherOccupation = table.Column<string>(type: "TEXT", nullable: true),
                    FatherEducationLevelYears = table.Column<int>(type: "INTEGER", nullable: true),
                    FatherOccupation = table.Column<string>(type: "TEXT", nullable: true),
                    PriorLiveBirths = table.Column<int>(type: "INTEGER", nullable: false),
                    PriorFetalDeaths = table.Column<int>(type: "INTEGER", nullable: false),
                    PrenatalVisitCount = table.Column<int>(type: "INTEGER", nullable: true),
                    MedicalCareBeganDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DateOfLastLiveBirth = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaternalStatistics", x => x.BirthRecordId);
                    table.ForeignKey(
                        name: "FK_MaternalStatistics_BirthRecords_BirthRecordId",
                        column: x => x.BirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NeonatalOutcomes",
                columns: table => new
                {
                    BirthRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeathDateUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IcdPmTiming = table.Column<string>(type: "TEXT", nullable: false),
                    IcdPmCauseCode = table.Column<string>(type: "TEXT", nullable: false),
                    ContributingMaternalConditionCode = table.Column<string>(type: "TEXT", nullable: true),
                    RecordedByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NeonatalOutcomes", x => x.BirthRecordId);
                    table.ForeignKey(
                        name: "FK_NeonatalOutcomes_BirthRecords_BirthRecordId",
                        column: x => x.BirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NeonatalOutcomes_Registrars_RecordedByRegistrarId",
                        column: x => x.RecordedByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TransactionId",
                table: "AuditLogs",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecords_Brn",
                table: "BirthRecords",
                column: "Brn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecords_ChildPersonId",
                table: "BirthRecords",
                column: "ChildPersonId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecords_FacilityId",
                table: "BirthRecords",
                column: "FacilityId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecords_FatherPersonId",
                table: "BirthRecords",
                column: "FatherPersonId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecords_MotherPersonId",
                table: "BirthRecords",
                column: "MotherPersonId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecords_RegisteredByRegistrarId",
                table: "BirthRecords",
                column: "RegisteredByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_Certificates_BirthRecordId",
                table: "Certificates",
                column: "BirthRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaternalOutcomes_RecordedByRegistrarId",
                table: "MaternalOutcomes",
                column: "RecordedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_NeonatalOutcomes_RecordedByRegistrarId",
                table: "NeonatalOutcomes",
                column: "RecordedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_Registrars_FacilityId",
                table: "Registrars",
                column: "FacilityId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLogs_TransactionId",
                table: "RequestLogs",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncBatches_FacilityId",
                table: "SyncBatches",
                column: "FacilityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "Certificates");

            migrationBuilder.DropTable(
                name: "MaternalOutcomes");

            migrationBuilder.DropTable(
                name: "MaternalStatistics");

            migrationBuilder.DropTable(
                name: "NeonatalOutcomes");

            migrationBuilder.DropTable(
                name: "RequestLogs");

            migrationBuilder.DropTable(
                name: "SyncBatches");

            migrationBuilder.DropTable(
                name: "BirthRecords");

            migrationBuilder.DropTable(
                name: "People");

            migrationBuilder.DropTable(
                name: "Registrars");

            migrationBuilder.DropTable(
                name: "Facilities");
        }
    }
}
