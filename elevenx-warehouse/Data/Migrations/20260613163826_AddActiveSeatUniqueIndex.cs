using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ElevenX.Warehouse.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveSeatUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_active_seat",
                table: "LicenseAssignments",
                columns: new[] { "ItemId", "AssignedToId" },
                unique: true,
                filter: "\"ReleasedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_active_seat",
                table: "LicenseAssignments");
        }
    }
}
