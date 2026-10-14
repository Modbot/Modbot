using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class BlueskyLineup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "bluesky_lineup_day",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bluesky_lineup_handled_through",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "bluesky_lineup_hour",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 12);

            migrationBuilder.AddColumn<bool>(
                name: "bluesky_lineup_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_lineup_time_zone",
                table: "settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bluesky_lineup_day",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_lineup_handled_through",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_lineup_hour",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_lineup_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_lineup_time_zone",
                table: "settings");
        }
    }
}
