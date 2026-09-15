using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "audit_log_read_at",
                table: "discord_server",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "audit_log_read_through",
                table: "discord_server",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "members_listed_at",
                table: "discord_server",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "seen_through",
                table: "discord_server",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "discord_member",
                columns: table => new
                {
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    global_name = table.Column<string>(type: "text", nullable: true),
                    nickname = table.Column<string>(type: "text", nullable: true),
                    avatar_url = table.Column<string>(type: "text", nullable: true),
                    is_bot = table.Column<bool>(type: "boolean", nullable: false),
                    is_pending = table.Column<bool>(type: "boolean", nullable: false),
                    boosting_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    left_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    roles = table.Column<string>(type: "jsonb", nullable: false),
                    timed_out_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voice_channel_id = table.Column<string>(type: "text", nullable: true),
                    voice_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_member", x => new { x.guild_id, x.user_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_member_current",
                table: "discord_member",
                columns: new[] { "guild_id", "left_at" });

            migrationBuilder.CreateIndex(
                name: "ix_discord_member_in_voice",
                table: "discord_member",
                column: "guild_id",
                filter: "voice_channel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_discord_member_roles",
                table: "discord_member",
                column: "roles")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_member");

            migrationBuilder.DropColumn(
                name: "audit_log_read_at",
                table: "discord_server");

            migrationBuilder.DropColumn(
                name: "audit_log_read_through",
                table: "discord_server");

            migrationBuilder.DropColumn(
                name: "members_listed_at",
                table: "discord_server");

            migrationBuilder.DropColumn(
                name: "seen_through",
                table: "discord_server");
        }
    }
}
