using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class BlueskyPostCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bluesky_counts_error",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bluesky_counts_error_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bluesky_counts_stopped_until",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "counted_at",
                table: "post_destination",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "like_count",
                table: "post_destination",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "reply_count",
                table: "post_destination",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "repost_count",
                table: "post_destination",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bluesky_counts_error",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_counts_error_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "bluesky_counts_stopped_until",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "counted_at",
                table: "post_destination");

            migrationBuilder.DropColumn(
                name: "like_count",
                table: "post_destination");

            migrationBuilder.DropColumn(
                name: "reply_count",
                table: "post_destination");

            migrationBuilder.DropColumn(
                name: "repost_count",
                table: "post_destination");
        }
    }
}
