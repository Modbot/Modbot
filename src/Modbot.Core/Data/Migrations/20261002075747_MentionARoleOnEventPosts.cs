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

            migrationBuilder.AddColumn<string>(
                name: "mention_role_id",
                table: "calendar_event",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "calendar_role_ping",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pinged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_role_ping", x => new { x.event_id, x.starts_at });
                    table.ForeignKey(
                        name: "fk_calendar_role_ping_calendar_event_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_role_ping");

            migrationBuilder.DropColumn(
                name: "bot_can_mention_everyone",
                table: "discord_server");

            migrationBuilder.DropColumn(
                name: "mentionable",
                table: "discord_role");

            migrationBuilder.DropColumn(
                name: "mention_role_id",
                table: "calendar_event");
        }
    }
}
