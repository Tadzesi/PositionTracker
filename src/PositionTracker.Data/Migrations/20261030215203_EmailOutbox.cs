using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PositionTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmailOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinishedAt",
                table: "Outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "Outbox",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReplyToAddress",
                table: "Outbox",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReplyToName",
                table: "Outbox",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_FinishedAt",
                table: "Outbox",
                column: "FinishedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Outbox_FinishedAt",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "FinishedAt",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "ReplyToAddress",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "ReplyToName",
                table: "Outbox");
        }
    }
}
