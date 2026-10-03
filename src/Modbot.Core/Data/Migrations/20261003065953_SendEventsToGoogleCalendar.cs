using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SendEventsToGoogleCalendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "google_removing_events",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "google_sending_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "google_calendar_id",
                table: "calendar_event_place",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "publish_to_google",
                table: "calendar_event",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "google_error",
                table: "calendar_date_change",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "google_error_at",
                table: "calendar_date_change",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_failed_fingerprint",
                table: "calendar_date_change",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_sent_fingerprint",
                table: "calendar_date_change",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "google_removing_events",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_sending_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "google_calendar_id",
                table: "calendar_event_place");

            migrationBuilder.DropColumn(
                name: "publish_to_google",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "google_error",
                table: "calendar_date_change");

            migrationBuilder.DropColumn(
                name: "google_error_at",
                table: "calendar_date_change");

            migrationBuilder.DropColumn(
                name: "google_failed_fingerprint",
                table: "calendar_date_change");

            migrationBuilder.DropColumn(
                name: "google_sent_fingerprint",
                table: "calendar_date_change");
        }
    }
}
