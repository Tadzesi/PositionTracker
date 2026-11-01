using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PositionTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResultRows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Bib = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Nickname = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Gender = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Category = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Wave = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    IsMinor = table.Column<bool>(type: "boolean", nullable: false),
                    NameConsent = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: true),
                    CategoryRank = table.Column<int>(type: "integer", nullable: true),
                    GenderRank = table.Column<int>(type: "integer", nullable: true),
                    Start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NetTime = table.Column<TimeSpan>(type: "interval", nullable: true),
                    GapToLeader = table.Column<TimeSpan>(type: "interval", nullable: true),
                    DistanceM = table.Column<double>(type: "double precision", nullable: false),
                    Laps = table.Column<int>(type: "integer", nullable: false),
                    AvgSpeedMps = table.Column<double>(type: "double precision", nullable: false),
                    MaxSpeedMps = table.Column<double>(type: "double precision", nullable: false),
                    MovingTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    StoppedTime = table.Column<TimeSpan>(type: "interval", nullable: false),
                    AscentM = table.Column<double>(type: "double precision", nullable: false),
                    DescentM = table.Column<double>(type: "double precision", nullable: false),
                    SplitsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResultRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResultRows_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResultRows_EventId_ParticipantId",
                table: "ResultRows",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResultRows");
        }
    }
}
