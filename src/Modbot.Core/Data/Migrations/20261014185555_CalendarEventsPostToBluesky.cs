using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CalendarEventsPostToBluesky : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bluesky_description",
                table: "calendar_event",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "bluesky_post_before_minutes",
                table: "calendar_event",
                type: "integer",
                nullable: false,
                defaultValue: 1440);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_title",
                table: "calendar_event",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "publish_to_bluesky",
                table: "calendar_event",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bluesky_description",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "bluesky_post_before_minutes",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "bluesky_title",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "publish_to_bluesky",
                table: "calendar_event");
        }
    }
}
