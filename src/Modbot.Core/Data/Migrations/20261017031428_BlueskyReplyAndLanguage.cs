using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class BlueskyReplyAndLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bluesky_languages",
                table: "calendar_event",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "bluesky_quotes_off",
                table: "calendar_event",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_who_can_reply",
                table: "calendar_event",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bluesky_languages",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "bluesky_quotes_off",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "bluesky_who_can_reply",
                table: "calendar_event");
        }
    }
}
