using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUnusualActivityAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "modbot_alert",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    watcher = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    now = table.Column<decimal>(type: "numeric", nullable: false),
                    normal = table.Column<decimal>(type: "numeric", nullable: false),
                    spread = table.Column<decimal>(type: "numeric", nullable: false),
                    score = table.Column<decimal>(type: "numeric", nullable: false),
                    sensitivity = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    figures = table.Column<string>(type: "jsonb", nullable: false),
                    link = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    text = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    dismissed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dismissed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dismissed_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    discord_channel_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    discord_posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    discord_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_alert", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "modbot_alert_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    discord_channel_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    quiet_hours = table.Column<int>(type: "integer", nullable: false),
                    write_sentence = table.Column<bool>(type: "boolean", nullable: false),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_alert_settings", x => x.id);
                    table.CheckConstraint("ck_modbot_alert_settings_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "modbot_alert_watch",
                columns: table => new
                {
                    watcher = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sensitivity = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    checked_through = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_alert_watch", x => x.watcher);
                });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_alert_discord_waiting",
                table: "modbot_alert",
                column: "at",
                filter: "discord_channel_id IS NOT NULL AND discord_posted_at IS NULL AND discord_error IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_modbot_alert_watcher_at",
                table: "modbot_alert",
                columns: new[] { "watcher", "at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "modbot_alert");

            migrationBuilder.DropTable(
                name: "modbot_alert_settings");

            migrationBuilder.DropTable(
                name: "modbot_alert_watch");
        }
    }
}
