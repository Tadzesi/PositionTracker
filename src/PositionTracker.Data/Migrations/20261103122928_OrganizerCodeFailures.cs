using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PositionTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrganizerCodeFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CodeFailures",
                table: "Organizers",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CodeFailures",
                table: "Organizers");
        }
    }
}
