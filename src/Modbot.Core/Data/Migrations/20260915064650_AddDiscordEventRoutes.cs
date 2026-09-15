using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Replaces the single moderation log channel with routes: any number of channels, each with
    /// its own event types and filters (Discord event routes design §8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-edited after scaffolding. EF put the column drops first; they now come last, after the
    /// data step has read them.
    /// </para>
    /// <para>
    /// <strong>An existing log channel becomes one route</strong> with the types it was sending,
    /// worked out the way <c>ModerationLogEvents.Parse</c> did: a null choice was all nine
    /// moderation types; anything else was a comma-separated list, trimmed, cut down to those nine
    /// and kept in their order. An empty choice sent nothing and becomes a route with no types,
    /// which sends nothing.
    /// </para>
    /// <para>
    /// <strong>The channel keeps its place in the fact log</strong>, so the first pass after the
    /// upgrade neither posts again what went out before it nor skips what came in during it. A log
    /// channel that had not started yet has no place, and starts from the newest fact, as it would
    /// have.
    /// </para>
    /// </remarks>
    public partial class AddDiscordEventRoutes : Migration
    {
        /// <summary>The old closed list, in its order. Written out because a migration must not change when the code does.</summary>
        private const string OldModerationTypes =
            "ARRAY['vrchat.group.member.ban', 'vrchat.group.member.unban', 'vrchat.group.member.remove', "
            + "'vrchat.group.instance.kick', 'vrchat.group.instance.warn', 'vrchat.group.request.reject', "
            + "'vrchat.group.request.block', 'vrchat.group.role.assign', 'vrchat.group.role.unassign']";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discord_event_channel",
                columns: table => new
                {
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    posted_through = table.Column<long>(type: "bigint", nullable: false),
                    last_posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retry_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_event_channel", x => x.channel_id);
                });

            migrationBuilder.CreateTable(
                name: "discord_event_route",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    event_types = table.Column<string>(type: "jsonb", nullable: false),
                    subject_ids = table.Column<string>(type: "jsonb", nullable: false),
                    actor_ids = table.Column<string>(type: "jsonb", nullable: false),
                    actor_automatic = table.Column<bool>(type: "boolean", nullable: false),
                    subject_vr_chat_role_ids = table.Column<string>(type: "jsonb", nullable: false),
                    actor_vr_chat_role_ids = table.Column<string>(type: "jsonb", nullable: false),
                    actor_modbot_role_ids = table.Column<string>(type: "jsonb", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discord_event_route", x => x.id);
                });

            migrationBuilder.Sql($"""
                INSERT INTO discord_event_route (
                    id, name, channel_id, enabled, event_types, subject_ids, actor_ids, actor_automatic,
                    subject_vr_chat_role_ids, actor_vr_chat_role_ids, actor_modbot_role_ids, position)
                SELECT
                    gen_random_uuid(),
                    NULL,
                    btrim(s.discord_log_channel_id),
                    TRUE,
                    COALESCE((
                        SELECT jsonb_agg(old.type ORDER BY old.ord)
                        FROM unnest({OldModerationTypes}) WITH ORDINALITY AS old(type, ord)
                        WHERE s.discord_log_event_types IS NULL
                           OR old.type IN (
                               SELECT btrim(chosen)
                               FROM unnest(string_to_array(s.discord_log_event_types, ',')) AS chosen)
                    ), '[]'::jsonb),
                    '[]'::jsonb,
                    '[]'::jsonb,
                    FALSE,
                    '[]'::jsonb,
                    '[]'::jsonb,
                    '[]'::jsonb,
                    0
                FROM settings s
                WHERE s.id = 1
                  AND s.discord_log_channel_id IS NOT NULL
                  AND btrim(s.discord_log_channel_id) <> '';
                """);

            migrationBuilder.Sql("""
                INSERT INTO discord_event_channel (channel_id, posted_through)
                SELECT btrim(s.discord_log_channel_id), s.discord_log_posted_through
                FROM settings s
                WHERE s.id = 1
                  AND s.discord_log_channel_id IS NOT NULL
                  AND btrim(s.discord_log_channel_id) <> ''
                  AND s.discord_log_posted_through IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "discord_log_channel_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_log_event_types",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "discord_log_posted_through",
                table: "settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "discord_log_channel_id",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "discord_log_event_types",
                table: "settings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "discord_log_posted_through",
                table: "settings",
                type: "bigint",
                nullable: true);

            // The first enabled route goes back to being the log channel, with its place. The old
            // column had no filters, so any the route had are lost, and types outside the old list
            // are dropped when the old code reads them.
            migrationBuilder.Sql("""
                UPDATE settings s
                SET discord_log_channel_id = r.channel_id,
                    discord_log_event_types = COALESCE(
                        (SELECT string_agg(t, ',') FROM jsonb_array_elements_text(r.event_types) AS t), ''),
                    discord_log_posted_through = c.posted_through
                FROM (
                    SELECT channel_id, event_types
                    FROM discord_event_route
                    WHERE enabled
                    ORDER BY position, id
                    LIMIT 1) AS r
                LEFT JOIN discord_event_channel c ON c.channel_id = r.channel_id
                WHERE s.id = 1;
                """);

            migrationBuilder.DropTable(
                name: "discord_event_channel");

            migrationBuilder.DropTable(
                name: "discord_event_route");
        }
    }
}
