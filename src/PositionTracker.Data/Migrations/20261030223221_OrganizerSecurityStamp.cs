using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PositionTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrganizerSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SecurityStamp",
                table: "Organizers",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Organizers");
        }
    }
}
