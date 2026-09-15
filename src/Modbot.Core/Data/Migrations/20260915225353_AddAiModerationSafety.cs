using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiModerationSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ai_acknowledged_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ai_acknowledged_by_user_id",
                table: "settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_acknowledged_by_username",
                table: "settings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "channel_mode",
                table: "ai_topic",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "all");

            migrationBuilder.AddColumn<string>(
                name: "channels",
                table: "ai_topic",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "exempt_roles",
                table: "ai_topic",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "exempt_roles_skip_flag",
                table: "ai_topic",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "paused_at",
                table: "ai_topic",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "paused_reason",
                table: "ai_topic",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "trial_days",
                table: "ai_topic",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "trial_ended_at",
                table: "ai_topic",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "trial_ended_by_user_id",
                table: "ai_topic",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trial_ended_by_username",
                table: "ai_topic",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "trial_started_at",
                table: "ai_topic",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "ai_topic",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "channel_mode",
                table: "ai_term_list",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "all");

            migrationBuilder.AddColumn<string>(
                name: "channels",
                table: "ai_term_list",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "exempt_roles",
                table: "ai_term_list",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "exempt_roles_skip_flag",
                table: "ai_term_list",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "paused_at",
                table: "ai_term_list",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "paused_reason",
                table: "ai_term_list",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "trial_days",
                table: "ai_term_list",
                type: "integer",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "trial_ended_at",
                table: "ai_term_list",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "trial_ended_by_user_id",
                table: "ai_term_list",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "trial_ended_by_username",
                table: "ai_term_list",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "trial_started_at",
                table: "ai_term_list",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "ai_term_list",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "rule_version",
                table: "ai_flag",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "trial",
                table: "ai_flag",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "would_delete_message",
                table: "ai_flag",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "would_time_out_minutes",
                table: "ai_flag",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_rule_version",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    text = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_rule_version", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_test_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ran_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    rule_version = table.Column<int>(type: "integer", nullable: false),
                    samples = table.Column<int>(type: "integer", nullable: false),
                    should_flag_count = table.Column<int>(type: "integer", nullable: false),
                    caught = table.Column<int>(type: "integer", nullable: false),
                    missed = table.Column<int>(type: "integer", nullable: false),
                    should_not_flag_count = table.Column<int>(type: "integer", nullable: false),
                    wrongly_flagged = table.Column<int>(type: "integer", nullable: false),
                    ai_skipped = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    results = table.Column<string>(type: "jsonb", nullable: false),
                    ran_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ran_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_test_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_test_sample",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    should_flag = table.Column<bool>(type: "boolean", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    target = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    seeded = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_test_sample", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_flag_rule_time",
                table: "ai_flag",
                columns: new[] { "rule_id", "flagged_at" });

            migrationBuilder.CreateIndex(
                name: "ux_ai_rule_version",
                table: "ai_rule_version",
                columns: new[] { "rule_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_test_run_rule",
                table: "ai_test_run",
                columns: new[] { "rule_id", "ran_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_test_sample_rule",
                table: "ai_test_sample",
                columns: new[] { "rule_id", "created_at" });

            // Every rule that already exists is version 1, so give each one a version row. Without
            // it a flag written tomorrow would name a version with no text behind it, and the Flags
            // page could not show the rule as it was.
            migrationBuilder.Sql(
                """
                INSERT INTO ai_rule_version (id, rule_kind, rule_id, version, changed_at, name, text, snapshot)
                SELECT gen_random_uuid(), 'termList', l.id, 1, l.updated_at, l.name,
                       left(coalesce((
                           SELECT string_agg(coalesce(t->>'text', t->>'pattern', ''), ', ')
                           FROM jsonb_array_elements(l.terms) AS t
                           WHERE NOT (l.excluded_terms @> to_jsonb(array[t->>'id']))
                       ), ''), 8000),
                       jsonb_build_object('kind', 'termList', 'carriedOver', true)
                FROM ai_term_list AS l;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO ai_rule_version (id, rule_kind, rule_id, version, changed_at, name, text, snapshot)
                SELECT gen_random_uuid(), 'topic', t.id, 1, t.updated_at, t.name, left(t.instructions, 8000),
                       jsonb_build_object('kind', 'topic', 'carriedOver', true)
                FROM ai_topic AS t;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_rule_version");

            migrationBuilder.DropTable(
                name: "ai_test_run");

            migrationBuilder.DropTable(
                name: "ai_test_sample");

            migrationBuilder.DropIndex(
                name: "ix_ai_flag_rule_time",
                table: "ai_flag");

            migrationBuilder.DropColumn(
                name: "ai_acknowledged_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_acknowledged_by_user_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_acknowledged_by_username",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "channel_mode",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "channels",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "exempt_roles",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "exempt_roles_skip_flag",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "paused_at",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "paused_reason",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "trial_days",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "trial_ended_at",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "trial_ended_by_user_id",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "trial_ended_by_username",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "trial_started_at",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "version",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "channel_mode",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "channels",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "exempt_roles",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "exempt_roles_skip_flag",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "paused_at",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "paused_reason",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "trial_days",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "trial_ended_at",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "trial_ended_by_user_id",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "trial_ended_by_username",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "trial_started_at",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "version",
                table: "ai_term_list");

            migrationBuilder.DropColumn(
                name: "rule_version",
                table: "ai_flag");

            migrationBuilder.DropColumn(
                name: "trial",
                table: "ai_flag");

            migrationBuilder.DropColumn(
                name: "would_delete_message",
                table: "ai_flag");

            migrationBuilder.DropColumn(
                name: "would_time_out_minutes",
                table: "ai_flag");
        }
    }
}
