using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class BlueskyPostGif : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "klipy_key",
                table: "settings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_gif_alt",
                table: "calendar_event",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_gif_url",
                table: "calendar_event",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "klipy_key",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_gif_alt",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "bluesky_gif_url",
                table: "calendar_event");
        }
    }
}
