using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class BlueskyPostPictures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bluesky_picture_alts",
                table: "calendar_event",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_picture_ids",
                table: "calendar_event",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bluesky_picture_alts",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "bluesky_picture_ids",
                table: "calendar_event");
        }
    }
}
