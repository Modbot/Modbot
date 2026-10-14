using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class BlueskyTagsAndFirstJoin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bluesky_pin_checked_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "bluesky_pin_next_event",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_pinned_uri",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_post_tags",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "first_join_bluesky_post_error",
                table: "calendar_opening",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_join_bluesky_posted_at",
                table: "calendar_opening",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "announce_first_join_on_bluesky",
                table: "calendar_event",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_tag",
                table: "calendar_category",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bluesky_pin_checked_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_pin_next_event",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_pinned_uri",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_post_tags",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "first_join_bluesky_post_error",
                table: "calendar_opening");

            migrationBuilder.DropColumn(
                name: "first_join_bluesky_posted_at",
                table: "calendar_opening");

            migrationBuilder.DropColumn(
                name: "announce_first_join_on_bluesky",
                table: "calendar_event");

            migrationBuilder.DropColumn(
                name: "bluesky_tag",
                table: "calendar_category");
        }
    }
}
