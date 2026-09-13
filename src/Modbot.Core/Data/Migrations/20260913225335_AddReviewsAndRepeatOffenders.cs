using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The second half of M2.5 (spec 5.8.4, 5.8.5): per-person action counts, reviews of a
    /// moderator's pattern, the per-moderator baseline they are compared against, the detection
    /// run's watermark, and the thresholds column on settings.
    /// </summary>
    /// <remarks>
    /// Three of the four tables are caches rebuilt from the fact log and the daily totals; only
    /// <c>modbot_review</c> holds anything that is not derived, and even that is mirrored into
    /// the fact log as <c>modbot.review.opened</c> / <c>.closed</c>. Nothing here needs data
    /// carried over: the first detection run after upgrading fills the caches.
    /// </remarks>
    public partial class AddReviewsAndRepeatOffenders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "review_thresholds",
                table: "settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "modbot_moderator_baseline",
                columns: table => new
                {
                    platform = table.Column<short>(type: "smallint", nullable: false),
                    moderator_id = table.Column<string>(type: "text", nullable: false),
                    actions = table.Column<decimal>(type: "numeric", nullable: false),
                    active_days = table.Column<int>(type: "integer", nullable: false),
                    actions_per_active_day = table.Column<decimal>(type: "numeric", nullable: false),
                    first_day = table.Column<DateOnly>(type: "date", nullable: true),
                    last_day = table.Column<DateOnly>(type: "date", nullable: true),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_moderator_baseline", x => new { x.platform, x.moderator_id });
                });

            migrationBuilder.CreateTable(
                name: "modbot_repeat_offender",
                columns: table => new
                {
                    subject_platform = table.Column<short>(type: "smallint", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    instance_kicks = table.Column<int>(type: "integer", nullable: false),
                    warns = table.Column<int>(type: "integer", nullable: false),
                    bans = table.Column<int>(type: "integer", nullable: false),
                    unbans = table.Column<int>(type: "integer", nullable: false),
                    removals = table.Column<int>(type: "integer", nullable: false),
                    rejections = table.Column<int>(type: "integer", nullable: false),
                    actions = table.Column<int>(type: "integer", nullable: false),
                    actions_last_30_days = table.Column<int>(type: "integer", nullable: false),
                    actions_last_90_days = table.Column<int>(type: "integer", nullable: false),
                    moderators = table.Column<int>(type: "integer", nullable: false),
                    moderators_last_90_days = table.Column<int>(type: "integer", nullable: false),
                    first_action_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_action_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_action_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    last_actor_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    counts_change_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_repeat_offender", x => new { x.subject_platform, x.subject_id });
                });

            migrationBuilder.CreateTable(
                name: "modbot_review",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    moderator_platform = table.Column<short>(type: "smallint", nullable: false),
                    moderator_id = table.Column<string>(type: "text", nullable: false),
                    signal = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    about = table.Column<string>(type: "text", nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    window_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<short>(type: "smallint", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_review", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "modbot_review_run_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    observed_through = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_review_run_state", x => x.id);
                    table.CheckConstraint("ck_modbot_review_run_state_singleton", "id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_repeat_offender_counts_change",
                table: "modbot_repeat_offender",
                column: "counts_change_at");

            migrationBuilder.CreateIndex(
                name: "ix_modbot_repeat_offender_last_action",
                table: "modbot_repeat_offender",
                column: "last_action_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_modbot_review_key",
                table: "modbot_review",
                columns: new[] { "moderator_platform", "moderator_id", "signal", "about", "window_end" });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_review_state",
                table: "modbot_review",
                columns: new[] { "state", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ux_modbot_review_open",
                table: "modbot_review",
                columns: new[] { "moderator_platform", "moderator_id", "signal", "about" },
                unique: true,
                filter: "state = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "modbot_moderator_baseline");

            migrationBuilder.DropTable(
                name: "modbot_repeat_offender");

            migrationBuilder.DropTable(
                name: "modbot_review");

            migrationBuilder.DropTable(
                name: "modbot_review_run_state");

            migrationBuilder.DropColumn(
                name: "review_thresholds",
                table: "settings");
        }
    }
}
