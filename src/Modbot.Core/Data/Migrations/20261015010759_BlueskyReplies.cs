using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class BlueskyReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bluesky_replies_checked_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bluesky_replies_error",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bluesky_replies_error_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bluesky_replies_read_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bluesky_replies_stopped_until",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bluesky_replies_checked_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_replies_error",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_replies_error_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_replies_read_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_replies_stopped_until",
                table: "settings");
        }
    }
}
