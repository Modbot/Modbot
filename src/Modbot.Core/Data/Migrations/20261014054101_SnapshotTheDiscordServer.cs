using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotTheDiscordServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discord_snapshot_claimed_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "discord_snapshot_restore_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "discord_snapshot_schedule",
                table: "settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "off");

            migrationBuilder.CreateTable(
                name: "discord_snapshot",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    scheduled = table.Column<bool>(type: "boolean", nullable: false),
                    taken_by = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    role_count = table.Column<int>(type: "integer", nullable: false),
                    channel_count = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_snapshot", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_snapshot_taken",
                table: "discord_snapshot",
                column: "taken_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_snapshot");

            migrationBuilder.DropColumn(
                name: "discord_snapshot_claimed_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_snapshot_restore_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_snapshot_schedule",
                table: "settings");
        }
    }
}
