using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventRecaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "event_recap_channel_id",
                table: "settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "event_recap_problem",
                table: "settings",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "event_recaps_from",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "event_recaps_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "recap_posted_at",
                table: "calendar_opening",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "event_recap_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "event_recap_problem",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "event_recaps_from",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "event_recaps_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "recap_posted_at",
                table: "calendar_opening");
        }
    }
}
