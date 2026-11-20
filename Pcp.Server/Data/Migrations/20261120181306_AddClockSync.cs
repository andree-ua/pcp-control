using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pcp.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClockSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClockOffsetMs",
                table: "Screens",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClockOffsetMs",
                table: "Screens");
        }
    }
}
