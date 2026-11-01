using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PositionTracker.Data.Migrations
{
    /// <inheritdoc />
    public partial class ResultsStartObserved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "StartObservedM",
                table: "ResultRows",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartObservedM",
                table: "ResultRows");
        }
    }
}
