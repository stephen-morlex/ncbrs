using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCBRS.Migrations
{
    /// <inheritdoc />
    public partial class RenameDeviceAlertDistrictToCountyCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DistrictId",
                table: "DeviceAlerts",
                newName: "CountyCode");

            migrationBuilder.RenameIndex(
                name: "IX_DeviceAlerts_DistrictId_Status",
                table: "DeviceAlerts",
                newName: "IX_DeviceAlerts_CountyCode_Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CountyCode",
                table: "DeviceAlerts",
                newName: "DistrictId");

            migrationBuilder.RenameIndex(
                name: "IX_DeviceAlerts_CountyCode_Status",
                table: "DeviceAlerts",
                newName: "IX_DeviceAlerts_DistrictId_Status");
        }
    }
}
