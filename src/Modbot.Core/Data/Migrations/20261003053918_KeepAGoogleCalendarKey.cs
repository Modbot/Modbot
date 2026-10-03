using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class KeepAGoogleCalendarKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "google_calendar_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_calendar_name",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_calendar_time_zone",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "google_can_change",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "google_checked_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_client_email",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_key_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_private_key_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_problem",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_project_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_public",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "google_stopped_until",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "google_calendar_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_calendar_name",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_calendar_time_zone",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_can_change",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_checked_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_client_email",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_key_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_private_key_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_problem",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_project_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_public",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_stopped_until",
                table: "settings");
        }
    }
}
