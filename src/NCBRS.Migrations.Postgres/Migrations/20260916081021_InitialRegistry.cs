using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class InitialRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    AuditLogId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.AuditLogId);
                });

            migrationBuilder.CreateTable(
                name: "Facilities",
                columns: table => new
                {
                    FacilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Tier = table.Column<string>(type: "text", nullable: false),
                    DistrictId = table.Column<string>(type: "text", nullable: false),
                    ConnectivityProfile = table.Column<string>(type: "text", nullable: false),
                    BrnBlockStart = table.Column<long>(type: "bigint", nullable: false),
                    BrnBlockEnd = table.Column<long>(type: "bigint", nullable: false),
                    BrnBlockNextAvailable = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Facilities", x => x.FacilityId);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    IdempotencyRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "text", nullable: false),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "integer", nullable: true),
                    ResponseBody = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.IdempotencyRecordId);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    OutboxMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "text", nullable: false),
                    PartitionKey = table.Column<string>(type: "text", nullable: true),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DispatchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    LockedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.OutboxMessageId);
                });

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "text", nullable: false),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    NationalIdRef = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.PersonId);
                });

            migrationBuilder.CreateTable(
                name: "RequestLogs",
                columns: table => new
                {
                    RequestLogId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: true),
                    TransactionIdGenerated = table.Column<bool>(type: "boolean", nullable: false),
                    Method = table.Column<string>(type: "text", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLogs", x => x.RequestLogId);
                });

            migrationBuilder.CreateTable(
                name: "DeviceAlerts",
                columns: table => new
                {
                    DeviceAlertId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    FacilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    DistrictId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    RaisedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DaysSilentWhenRaised = table.Column<int>(type: "integer", nullable: false),
                    ThresholdDays = table.Column<int>(type: "integer", nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcknowledgementNote = table.Column<string>(type: "text", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceAlerts", x => x.DeviceAlertId);
                    table.ForeignKey(
                        name: "FK_DeviceAlerts_Facilities_FacilityId",
                        column: x => x.FacilityId,
                        principalTable: "Facilities",
                        principalColumn: "FacilityId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    FacilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    PublicKeyPem = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: true),
                    EnrolledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EnrolledByRegistrarId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StatusChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StatusChangedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: true),
                    StatusReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.DeviceId);
                    table.ForeignKey(
                        name: "FK_Devices_Facilities_FacilityId",
                        column: x => x.FacilityId,
                        principalTable: "Facilities",
                        principalColumn: "FacilityId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Registrars",
                columns: table => new
                {
                    RegistrarId = table.Column<Guid>(type: "uuid", nullable: false),
                    FacilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalSubjectId = table.Column<string>(type: "text", nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    CredentialHash = table.Column<string>(type: "text", nullable: true)
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
                    SyncBatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    FacilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false)
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
                    BirthRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Brn = table.Column<string>(type: "text", nullable: false),
                    VitalEventType = table.Column<string>(type: "text", nullable: false),
                    ChildPersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    MotherPersonId = table.Column<Guid>(type: "uuid", nullable: true),
                    FatherPersonId = table.Column<Guid>(type: "uuid", nullable: true),
                    FacilityId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegisteredByRegistrarId = table.Column<Guid>(type: "uuid", nullable: false),
                    DateOfBirth = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Sex = table.Column<string>(type: "text", nullable: false),
                    BirthWeightGrams = table.Column<int>(type: "integer", nullable: true),
                    GestationalAgeWeeks = table.Column<decimal>(type: "numeric", nullable: true),
                    Plurality = table.Column<string>(type: "text", nullable: false),
                    BirthOrder = table.Column<int>(type: "integer", nullable: true),
                    SupersededByBirthRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProvisionalIdentifier = table.Column<string>(type: "text", nullable: true),
                    ReconciledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedBySyncBatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnnulledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                name: "AmendmentConflicts",
                columns: table => new
                {
                    AmendmentConflictId = table.Column<Guid>(type: "uuid", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmendmentRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Field = table.Column<string>(type: "text", nullable: false),
                    ExpectedPreviousValue = table.Column<string>(type: "text", nullable: true),
                    ActualPreviousValue = table.Column<string>(type: "text", nullable: true),
                    ResolvedValue = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    DetectedFromRegistrarId = table.Column<Guid>(type: "uuid", nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "text", nullable: true),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AmendmentConflicts", x => x.AmendmentConflictId);
                    table.ForeignKey(
                        name: "FK_AmendmentConflicts_BirthRecords_BirthRecordId",
                        column: x => x.BirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AmendmentConflicts_Registrars_ReviewedByRegistrarId",
                        column: x => x.ReviewedByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BirthRecordAmendments",
                columns: table => new
                {
                    BirthRecordAmendmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Field = table.Column<string>(type: "text", nullable: false),
                    PreviousValue = table.Column<string>(type: "text", nullable: true),
                    NewValue = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    AmendedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmendedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AmendmentRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "text", nullable: true)
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
                    table.ForeignKey(
                        name: "FK_BirthRecordAmendments_Registrars_ReviewedByRegistrarId",
                        column: x => x.ReviewedByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Certificates",
                columns: table => new
                {
                    CertificateId = table.Column<Guid>(type: "uuid", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssueDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SignatureHash = table.Column<string>(type: "text", nullable: false),
                    QrPayload = table.Column<string>(type: "text", nullable: false),
                    ReprintCount = table.Column<int>(type: "integer", nullable: false),
                    WithdrawnAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WithdrawnReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Certificates", x => x.CertificateId);
                    table.ForeignKey(
                        name: "FK_Certificates_BirthRecords_BirthRecordId",
                        column: x => x.BirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DuplicateCandidates",
                columns: table => new
                {
                    DuplicateCandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    MatchedBirthRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    Reasons = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    DetectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "text", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "LateRegistrations",
                columns: table => new
                {
                    LateRegistrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    DaysLate = table.Column<int>(type: "integer", nullable: false),
                    WindowDaysAtFiling = table.Column<int>(type: "integer", nullable: false),
                    EvidenceType = table.Column<string>(type: "text", nullable: false),
                    EvidenceReference = table.Column<string>(type: "text", nullable: true),
                    DeclarantName = table.Column<string>(type: "text", nullable: false),
                    DeclarantRelationship = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    SubmittedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewNote = table.Column<string>(type: "text", nullable: true),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LateRegistrations", x => x.LateRegistrationId);
                    table.ForeignKey(
                        name: "FK_LateRegistrations_BirthRecords_BirthRecordId",
                        column: x => x.BirthRecordId,
                        principalTable: "BirthRecords",
                        principalColumn: "BirthRecordId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LateRegistrations_Registrars_ReviewedByRegistrarId",
                        column: x => x.ReviewedByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LateRegistrations_Registrars_SubmittedByRegistrarId",
                        column: x => x.SubmittedByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaternalOutcomes",
                columns: table => new
                {
                    BirthRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeathDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IcdMmCauseCode = table.Column<string>(type: "text", nullable: false),
                    RecordedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: false)
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
                    BirthRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    MotherEducationLevel = table.Column<string>(type: "text", nullable: true),
                    MotherOccupation = table.Column<string>(type: "text", nullable: true),
                    FatherEducationLevel = table.Column<string>(type: "text", nullable: true),
                    FatherOccupation = table.Column<string>(type: "text", nullable: true),
                    PriorLiveBirths = table.Column<int>(type: "integer", nullable: false),
                    PriorFetalDeaths = table.Column<int>(type: "integer", nullable: false),
                    PrenatalVisitCount = table.Column<int>(type: "integer", nullable: true),
                    MedicalCareBeganDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DateOfLastLiveBirth = table.Column<DateOnly>(type: "date", nullable: true),
                    RecordedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true)
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
                    table.ForeignKey(
                        name: "FK_MaternalStatistics_Registrars_RecordedByRegistrarId",
                        column: x => x.RecordedByRegistrarId,
                        principalTable: "Registrars",
                        principalColumn: "RegistrarId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NeonatalOutcomes",
                columns: table => new
                {
                    BirthRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeathDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IcdPmTiming = table.Column<string>(type: "text", nullable: false),
                    IcdPmCauseCode = table.Column<string>(type: "text", nullable: false),
                    ContributingMaternalConditionCode = table.Column<string>(type: "text", nullable: true),
                    RecordedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "RecordAnnulments",
                columns: table => new
                {
                    RecordAnnulmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Justification = table.Column<string>(type: "text", nullable: false),
                    AuthorityReference = table.Column<string>(type: "text", nullable: true),
                    AnnulledByRegistrarId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnnulledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CertificateRevoked = table.Column<bool>(type: "boolean", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "CertificateRevocations",
                columns: table => new
                {
                    CertificateRevocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CertificateId = table.Column<Guid>(type: "uuid", nullable: false),
                    BirthRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    SerialHash = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedByRegistrarId = table.Column<Guid>(type: "uuid", nullable: true),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: true)
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
                name: "IX_AmendmentConflicts_BirthRecordId",
                table: "AmendmentConflicts",
                column: "BirthRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_AmendmentConflicts_ReviewedByRegistrarId",
                table: "AmendmentConflicts",
                column: "ReviewedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_AmendmentConflicts_Status",
                table: "AmendmentConflicts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TransactionId",
                table: "AuditLogs",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecordAmendments_AmendedByRegistrarId",
                table: "BirthRecordAmendments",
                column: "AmendedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecordAmendments_AmendmentRequestId",
                table: "BirthRecordAmendments",
                column: "AmendmentRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecordAmendments_BirthRecordId_AmendedAtUtc",
                table: "BirthRecordAmendments",
                columns: new[] { "BirthRecordId", "AmendedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecordAmendments_ReviewedByRegistrarId",
                table: "BirthRecordAmendments",
                column: "ReviewedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecordAmendments_Status",
                table: "BirthRecordAmendments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecordAmendments_TransactionId",
                table: "BirthRecordAmendments",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_BirthRecords_AnnulledAtUtc",
                table: "BirthRecords",
                column: "AnnulledAtUtc");

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
                name: "IX_BirthRecords_DateOfBirth",
                table: "BirthRecords",
                column: "DateOfBirth");

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

            migrationBuilder.CreateIndex(
                name: "IX_Certificates_BirthRecordId",
                table: "Certificates",
                column: "BirthRecordId",
                unique: true,
                filter: "\"WithdrawnAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceAlerts_DeviceId",
                table: "DeviceAlerts",
                column: "DeviceId",
                unique: true,
                filter: "\"ResolvedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceAlerts_DistrictId_Status",
                table: "DeviceAlerts",
                columns: new[] { "DistrictId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceAlerts_FacilityId",
                table: "DeviceAlerts",
                column: "FacilityId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_FacilityId_LastSeenAtUtc",
                table: "Devices",
                columns: new[] { "FacilityId", "LastSeenAtUtc" });

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

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_CompletedAtUtc",
                table: "IdempotencyRecords",
                column: "CompletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_TransactionId",
                table: "IdempotencyRecords",
                column: "TransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LateRegistrations_BirthRecordId",
                table: "LateRegistrations",
                column: "BirthRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LateRegistrations_ReviewedByRegistrarId",
                table: "LateRegistrations",
                column: "ReviewedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_LateRegistrations_Status",
                table: "LateRegistrations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_LateRegistrations_SubmittedByRegistrarId",
                table: "LateRegistrations",
                column: "SubmittedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_MaternalOutcomes_RecordedByRegistrarId",
                table: "MaternalOutcomes",
                column: "RecordedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_MaternalStatistics_RecordedByRegistrarId",
                table: "MaternalStatistics",
                column: "RecordedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_NeonatalOutcomes_RecordedByRegistrarId",
                table: "NeonatalOutcomes",
                column: "RecordedByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_DispatchedAtUtc_CreatedAtUtc",
                table: "OutboxMessages",
                columns: new[] { "DispatchedAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RecordAnnulments_AnnulledByRegistrarId",
                table: "RecordAnnulments",
                column: "AnnulledByRegistrarId");

            migrationBuilder.CreateIndex(
                name: "IX_RecordAnnulments_BirthRecordId",
                table: "RecordAnnulments",
                column: "BirthRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registrars_ExternalSubjectId",
                table: "Registrars",
                column: "ExternalSubjectId",
                unique: true);

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
                name: "AmendmentConflicts");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "BirthRecordAmendments");

            migrationBuilder.DropTable(
                name: "CertificateRevocations");

            migrationBuilder.DropTable(
                name: "DeviceAlerts");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "DuplicateCandidates");

            migrationBuilder.DropTable(
                name: "IdempotencyRecords");

            migrationBuilder.DropTable(
                name: "LateRegistrations");

            migrationBuilder.DropTable(
                name: "MaternalOutcomes");

            migrationBuilder.DropTable(
                name: "MaternalStatistics");

            migrationBuilder.DropTable(
                name: "NeonatalOutcomes");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "RecordAnnulments");

            migrationBuilder.DropTable(
                name: "RequestLogs");

            migrationBuilder.DropTable(
                name: "SyncBatches");

            migrationBuilder.DropTable(
                name: "Certificates");

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
