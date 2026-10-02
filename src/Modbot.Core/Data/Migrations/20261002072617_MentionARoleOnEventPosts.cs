using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MentionARoleOnEventPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Both read as no until the bot next reads the whole server, which it does each time it
            // connects: the picker marks every role "Bot cannot mention" until then.
            migrationBuilder.AddColumn<bool>(
                name: "bot_can_mention_everyone",
                table: "discord_server",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "mentionable",
                table: "discord_role",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "role_mentioned_for",
                table: "calendar_event_place",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mention_role_id",
                table: "calendar_event",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bot_can_mention_everyone",
                table: "discord_server");

            migrationBuilder.DropColumn(
                name: "mentionable",
                table: "discord_role");

            migrationBuilder.DropColumn(
                name: "role_mentioned_for",
                table: "calendar_event_place");

            migrationBuilder.DropColumn(
                name: "mention_role_id",
                table: "calendar_event");
        }
    }
}
