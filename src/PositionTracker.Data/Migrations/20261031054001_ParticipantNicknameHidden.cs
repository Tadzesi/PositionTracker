using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PositionTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class ParticipantNicknameHidden : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NicknameHidden",
                table: "Participants",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NicknameHidden",
                table: "Participants");
        }
    }
}
