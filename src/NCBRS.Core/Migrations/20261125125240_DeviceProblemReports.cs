using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class DeviceProblemReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceProblemReports",
                columns: table => new
                {
                    DeviceProblemReportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    FacilityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CountyCode = table.Column<string>(type: "TEXT", nullable: false),
                    ReportedByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WrittenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    DiagnosticsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedByRegistrarId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResolutionNote = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceProblemReports", x => x.DeviceProblemReportId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceProblemReports_CountyCode_ResolvedAtUtc",
                table: "DeviceProblemReports",
                columns: new[] { "CountyCode", "ResolvedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceProblemReports");
        }
    }
}
