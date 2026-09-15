using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ai_moderation_calls_day",
                table: "settings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ai_moderation_calls_used",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ai_moderation_daily_call_limit",
                table: "settings",
                type: "integer",
                nullable: false,
                // The existing settings row starts where a new one does (Settings.AiModerationDailyCallLimit).
                defaultValue: 200);

            migrationBuilder.AddColumn<bool>(
                name: "ai_moderation_enabled",
                table: "settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ai_moderation_profile_facts_read_through",
                table: "settings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "ai_feature_limit",
                columns: table => new
                {
                    feature = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    monthly_token_limit = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_feature_limit", x => x.feature);
                });

            migrationBuilder.CreateTable(
                name: "ai_flag",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    flagged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rule_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    term_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    term = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    target = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject_platform = table.Column<short>(type: "smallint", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    subject_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    message_id = table.Column<string>(type: "text", nullable: true),
                    matched = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    message_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    timed_out_minutes = table.Column<int>(type: "integer", nullable: true),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    dismissed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dismissed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dismissed_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_flag", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_term_list",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    targets = table.Column<int>(type: "integer", nullable: false),
                    terms = table.Column<string>(type: "jsonb", nullable: false),
                    excluded_terms = table.Column<string>(type: "jsonb", nullable: false),
                    delete_message = table.Column<bool>(type: "boolean", nullable: false),
                    timeout_minutes = table.Column<int>(type: "integer", nullable: true),
                    act_set_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    act_set_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    act_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    hub_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    hub_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    hub_fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    hub_available_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    hub_available_terms = table.Column<string>(type: "jsonb", nullable: true),
                    hub_available_changes = table.Column<string>(type: "jsonb", nullable: true),
                    hub_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_term_list", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_topic",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    instructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    sensitivity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    targets = table.Column<int>(type: "integer", nullable: false),
                    delete_message = table.Column<bool>(type: "boolean", nullable: false),
                    timeout_minutes = table.Column<int>(type: "integer", nullable: true),
                    act_set_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    act_set_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    act_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_topic", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    feature = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    cached_input_tokens = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_flag_message",
                table: "ai_flag",
                column: "message_id",
                filter: "message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ai_flag_rule_person",
                table: "ai_flag",
                columns: new[] { "rule_id", "term_key", "subject_platform", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_flag_state",
                table: "ai_flag",
                columns: new[] { "state", "flagged_at" });

            migrationBuilder.CreateIndex(
                name: "ux_ai_term_list_hub_id",
                table: "ai_term_list",
                column: "hub_id",
                unique: true,
                filter: "hub_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_feature_at",
                table: "ai_usage",
                columns: new[] { "feature", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_feature_limit");

            migrationBuilder.DropTable(
                name: "ai_flag");

            migrationBuilder.DropTable(
                name: "ai_term_list");

            migrationBuilder.DropTable(
                name: "ai_topic");

            migrationBuilder.DropTable(
                name: "ai_usage");

            migrationBuilder.DropColumn(
                name: "ai_moderation_calls_day",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_moderation_calls_used",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_moderation_daily_call_limit",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_moderation_enabled",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ai_moderation_profile_facts_read_through",
                table: "settings");
        }
    }
}
