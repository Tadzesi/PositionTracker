using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PositionTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventPointToPoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PointToPoint",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // An open course could only ever be ridden once (start A, finish B): such events become point-to-point.
            migrationBuilder.Sql("""
                UPDATE "Events" AS e SET "PointToPoint" = TRUE
                FROM "Courses" AS c
                WHERE c."EventId" = e."Id" AND NOT c."IsClosed" AND e."Laps" = 1 AND e."GpxBytes" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PointToPoint",
                table: "Events");
        }
    }
}
