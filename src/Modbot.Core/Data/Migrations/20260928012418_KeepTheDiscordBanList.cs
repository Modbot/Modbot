using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class KeepTheDiscordBanList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bans_listed_at",
                table: "discord_server",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "discord_ban",
                columns: table => new
                {
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    username = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    avatar_url = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    banned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lifted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_ban", x => new { x.guild_id, x.user_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_ban_current",
                table: "discord_ban",
                columns: new[] { "guild_id", "lifted_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_ban");

            migrationBuilder.DropColumn(
                name: "bans_listed_at",
                table: "discord_server");
        }
    }
}
