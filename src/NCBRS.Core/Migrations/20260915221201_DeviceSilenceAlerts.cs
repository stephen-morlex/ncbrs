using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class DeviceSilenceAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceAlerts",
                columns: table => new
                {
                    DeviceAlertId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    FacilityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DistrictId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RaisedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DaysSilentWhenRaised = table.Column<int>(type: "INTEGER", nullable: false),
                    ThresholdDays = table.Column<int>(type: "INTEGER", nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AcknowledgedByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AcknowledgementNote = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceAlerts");
        }
    }
}
