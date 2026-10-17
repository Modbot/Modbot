using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class BlueskyPostVideo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bluesky_video_alt",
                table: "calendar_event",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "bluesky_video_id",
                table: "calendar_event",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_video_url",
                table: "calendar_event",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "calendar_bluesky_video",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_bluesky_video", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_bluesky_video_created_at",
                table: "calendar_bluesky_video",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_bluesky_video");

            migrationBuilder.DropColumn(
                name: "bluesky_video_alt",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "bluesky_video_id",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "bluesky_video_url",
                table: "calendar_event");
        }
    }
}
