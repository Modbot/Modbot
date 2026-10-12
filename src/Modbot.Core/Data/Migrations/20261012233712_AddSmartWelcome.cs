using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSmartWelcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_welcome_channel_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "discord_welcome_enabled",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "discord_welcome_started_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_welcome_via",
                table: "settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "dm");

            migrationBuilder.CreateTable(
                name: "discord_welcome",
                columns: table => new
                {
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_welcome", x => x.discord_user_id);
                });

            migrationBuilder.CreateTable(
                name: "discord_welcome_step",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    role_id = table.Column<string>(type: "text", nullable: true),
                    text = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    link = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_welcome_step", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_welcome_step_order",
                table: "discord_welcome_step",
                columns: new[] { "kind", "position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_welcome");

            migrationBuilder.DropTable(
                name: "discord_welcome_step");

            migrationBuilder.DropColumn(
                name: "discord_welcome_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_welcome_enabled",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_welcome_started_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_welcome_via",
                table: "settings");
        }
    }
}
