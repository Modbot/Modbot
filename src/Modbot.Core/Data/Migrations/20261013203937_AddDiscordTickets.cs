using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_ticket_transcript_channel_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "discord_ticket",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    discord_user_id = table.Column<string>(type: "text", nullable: false),
                    discord_user_name = table.Column<string>(type: "text", nullable: true),
                    thread_id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    holds_private_images = table.Column<bool>(type: "boolean", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_discord_user_id = table.Column<string>(type: "text", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_ticket", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "discord_ticket_type",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    staff_role_id = table.Column<string>(type: "text", nullable: true),
                    opening_message = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: false),
                    holds_private_images = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_ticket_type", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discord_ticket_thread",
                table: "discord_ticket",
                column: "thread_id");

            migrationBuilder.CreateIndex(
                name: "ux_discord_ticket_open",
                table: "discord_ticket",
                columns: new[] { "type_id", "discord_user_id" },
                unique: true,
                filter: "state = 'open'");

            migrationBuilder.CreateIndex(
                name: "ix_discord_ticket_type_order",
                table: "discord_ticket_type",
                column: "position");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discord_ticket");

            migrationBuilder.DropTable(
                name: "discord_ticket_type");

            migrationBuilder.DropColumn(
                name: "discord_ticket_transcript_channel_id",
                table: "settings");
        }
    }
}
