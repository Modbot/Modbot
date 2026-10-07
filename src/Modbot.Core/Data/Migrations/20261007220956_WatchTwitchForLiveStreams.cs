using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class WatchTwitchForLiveStreams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "twitch_channel_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "twitch_channel_login",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "twitch_channel_name",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "twitch_checked_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "twitch_client_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "twitch_client_secret_encrypted",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "twitch_live_on",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "twitch_poll_problem",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "twitch_polled_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "twitch_post_after_minutes",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "twitch_post_every_hours",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<string>(
                name: "twitch_post_places",
                table: "settings",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "twitch_post_text",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "twitch_post_title",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "twitch_problem",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "twitch_stopped_until",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_key",
                table: "post",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "twitch_stream",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    viewers = table.Column<int>(type: "integer", nullable: false),
                    peak_viewers = table.Column<int>(type: "integer", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_set_by_staff = table.Column<bool>(type: "boolean", nullable: false),
                    post_decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    post_id = table.Column<Guid>(type: "uuid", nullable: true),
                    update_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_twitch_stream", x => x.id);
                    table.ForeignKey(
                        name: "fk_twitch_stream_calendar_events_event_id",
                        column: x => x.event_id,
                        principalTable: "calendar_event",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ux_post_kind_external_key",
                table: "post",
                columns: new[] { "kind", "external_key" },
                unique: true,
                filter: "external_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_twitch_stream_event_id",
                table: "twitch_stream",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_twitch_stream_started_at",
                table: "twitch_stream",
                column: "started_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "twitch_stream");

            migrationBuilder.DropIndex(
                name: "ux_post_kind_external_key",
                table: "post");

            migrationBuilder.DropColumn(
                name: "twitch_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_channel_login",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_channel_name",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_checked_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_client_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_client_secret_encrypted",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_live_on",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_poll_problem",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_polled_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_post_after_minutes",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_post_every_hours",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_post_places",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_post_text",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_post_title",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_problem",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "twitch_stopped_until",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "external_key",
                table: "post");
        }
    }
}
