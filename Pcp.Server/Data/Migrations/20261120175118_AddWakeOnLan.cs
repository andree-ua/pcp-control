using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pcp.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWakeOnLan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsWired",
                table: "Screens",
                type: "tinyint(1)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastConnectionAt",
                table: "Screens",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocalIp",
                table: "Screens",
                type: "varchar(45)",
                maxLength: 45,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "MacAddress",
                table: "Screens",
                type: "varchar(17)",
                maxLength: 17,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "NetworkCidr",
                table: "Screens",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "SubnetMask",
                table: "Screens",
                type: "varchar(45)",
                maxLength: 45,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "WolEnabled",
                table: "Screens",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_Screens_NetworkCidr",
                table: "Screens",
                column: "NetworkCidr");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Screens_NetworkCidr",
                table: "Screens");

            migrationBuilder.DropColumn(
                name: "IsWired",
                table: "Screens");

            migrationBuilder.DropColumn(
                name: "LastConnectionAt",
                table: "Screens");

            migrationBuilder.DropColumn(
                name: "LocalIp",
                table: "Screens");

            migrationBuilder.DropColumn(
                name: "MacAddress",
                table: "Screens");

            migrationBuilder.DropColumn(
                name: "NetworkCidr",
                table: "Screens");

            migrationBuilder.DropColumn(
                name: "SubnetMask",
                table: "Screens");

            migrationBuilder.DropColumn(
                name: "WolEnabled",
                table: "Screens");
        }
    }
}
