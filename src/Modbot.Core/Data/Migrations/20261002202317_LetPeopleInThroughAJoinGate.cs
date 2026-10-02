using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LetPeopleInThroughAJoinGate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_gate_channel_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discord_gate_held_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "discord_gate_hold_on_spike",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "discord_gate_member_role_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_gate_message",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_gate_message_channel_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_gate_message_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_gate_message_posted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_gate_mode",
                table: "settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "off");

            migrationBuilder.AddColumn<bool>(
                name: "discord_gate_needs_eighteen_plus",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "discord_gate_needs_link",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "discord_gate_pause_invites",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "discord_gate_remove_after_minutes",
                table: "settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discord_gate_spike_seen_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discord_gate_started_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "discord_gate_entry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    discord_username = table.Column<string>(type: "text", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    watch_only = table.Column<bool>(type: "boolean", nullable: false),
                    pending = table.Column<bool>(type: "boolean", nullable: false),
                    agreed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    minutes_counted = table.Column<int>(type: "integer", nullable: false),
                    last_counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    warned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    would_remove_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    problem = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_gate_entry", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_gate_entry_waiting",
                table: "discord_gate_entry",
                column: "joined_at",
                filter: "closed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_discord_gate_entry_open",
                table: "discord_gate_entry",
                columns: new[] { "guild_id", "discord_user_id" },
                unique: true,
                filter: "closed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_gate_entry");

            migrationBuilder.DropColumn(
                name: "discord_gate_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_held_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_hold_on_spike",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_member_role_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_message",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_message_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_message_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_message_posted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_mode",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_needs_eighteen_plus",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_needs_link",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_pause_invites",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_remove_after_minutes",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_spike_seen_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_gate_started_at",
                table: "settings");
        }
    }
}
