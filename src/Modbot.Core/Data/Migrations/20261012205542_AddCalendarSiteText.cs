using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarSiteText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_description",
                table: "calendar_event",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_title",
                table: "calendar_event",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_description",
                table: "calendar_event",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "google_title",
                table: "calendar_event",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vr_chat_description",
                table: "calendar_event",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vr_chat_title",
                table: "calendar_event",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "discord_description",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "discord_title",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "google_description",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "google_title",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "vr_chat_description",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "vr_chat_title",
                table: "calendar_event");
        }
    }
}
