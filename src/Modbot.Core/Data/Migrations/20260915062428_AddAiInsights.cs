using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiInsights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "modbot_insight",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    first_day = table.Column<DateOnly>(type: "date", nullable: false),
                    last_day = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_by = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    figures = table.Column<string>(type: "jsonb", nullable: false),
                    text = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    discord_channel_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    discord_posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    discord_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_insight", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "modbot_insight_schedule",
                columns: table => new
                {
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    every = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    hour = table.Column<int>(type: "integer", nullable: false),
                    weekday = table.Column<int>(type: "integer", nullable: false),
                    discord_channel_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    handled_through = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_insight_schedule", x => x.kind);
                });

            migrationBuilder.CreateTable(
                name: "modbot_insight_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_insight_settings", x => x.id);
                    table.CheckConstraint("ck_modbot_insight_settings_singleton", "id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_insight_discord_waiting",
                table: "modbot_insight",
                column: "created_at",
                filter: "discord_channel_id IS NOT NULL AND discord_posted_at IS NULL AND discord_error IS NULL AND text IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_modbot_insight_kind_created",
                table: "modbot_insight",
                columns: new[] { "kind", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "modbot_insight");

            migrationBuilder.DropTable(
                name: "modbot_insight_schedule");

            migrationBuilder.DropTable(
                name: "modbot_insight_settings");
        }
    }
}
