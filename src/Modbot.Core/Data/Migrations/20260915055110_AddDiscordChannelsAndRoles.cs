using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordChannelsAndRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discord_channel",
                columns: table => new
                {
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    category_id = table.Column<string>(type: "text", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    nsfw = table.Column<bool>(type: "boolean", nullable: false),
                    bot_can_view = table.Column<bool>(type: "boolean", nullable: false),
                    bot_can_read_history = table.Column<bool>(type: "boolean", nullable: false),
                    bot_can_send = table.Column<bool>(type: "boolean", nullable: false),
                    bot_can_embed_links = table.Column<bool>(type: "boolean", nullable: false),
                    bot_can_attach_files = table.Column<bool>(type: "boolean", nullable: false),
                    bot_can_manage_messages = table.Column<bool>(type: "boolean", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_channel", x => x.channel_id);
                });

            migrationBuilder.CreateTable(
                name: "discord_role",
                columns: table => new
                {
                    role_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    color = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    managed = table.Column<bool>(type: "boolean", nullable: false),
                    everyone = table.Column<bool>(type: "boolean", nullable: false),
                    bot_can_assign = table.Column<bool>(type: "boolean", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_role", x => x.role_id);
                });

            migrationBuilder.CreateTable(
                name: "discord_server",
                columns: table => new
                {
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    bot_can_view_audit_log = table.Column<bool>(type: "boolean", nullable: false),
                    bot_can_manage_roles = table.Column<bool>(type: "boolean", nullable: false),
                    refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_server", x => x.guild_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_channel_guild",
                table: "discord_channel",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_discord_role_guild",
                table: "discord_role",
                column: "guild_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_channel");

            migrationBuilder.DropTable(
                name: "discord_role");

            migrationBuilder.DropTable(
                name: "discord_server");
        }
    }
}
