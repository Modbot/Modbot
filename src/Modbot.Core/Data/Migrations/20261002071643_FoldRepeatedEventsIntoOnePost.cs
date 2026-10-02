using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class FoldRepeatedEventsIntoOnePost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "repeat_actor_id",
                table: "discord_event_channel",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "repeat_actor_platform",
                table: "discord_event_channel",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "repeat_count",
                table: "discord_event_channel",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "repeat_fields",
                table: "discord_event_channel",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "repeat_first_at",
                table: "discord_event_channel",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "repeat_last_at",
                table: "discord_event_channel",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repeat_post_id",
                table: "discord_event_channel",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repeat_subject_id",
                table: "discord_event_channel",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "repeat_subject_platform",
                table: "discord_event_channel",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repeat_type",
                table: "discord_event_channel",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "repeat_actor_id",
                table: "discord_event_channel");

            migrationBuilder.DropColumn(
                name: "repeat_actor_platform",
                table: "discord_event_channel");

            migrationBuilder.DropColumn(
                name: "repeat_count",
                table: "discord_event_channel");

            migrationBuilder.DropColumn(
                name: "repeat_fields",
                table: "discord_event_channel");

            migrationBuilder.DropColumn(
                name: "repeat_first_at",
                table: "discord_event_channel");

            migrationBuilder.DropColumn(
                name: "repeat_last_at",
                table: "discord_event_channel");

            migrationBuilder.DropColumn(
                name: "repeat_post_id",
                table: "discord_event_channel");

            migrationBuilder.DropColumn(
                name: "repeat_subject_id",
                table: "discord_event_channel");

            migrationBuilder.DropColumn(
                name: "repeat_subject_platform",
                table: "discord_event_channel");

            migrationBuilder.DropColumn(
                name: "repeat_type",
                table: "discord_event_channel");
        }
    }
}
