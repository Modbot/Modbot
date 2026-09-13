using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Plain words in the database, to match the code: <c>modbot_rollup_daily</c> becomes
    /// <c>modbot_daily_total</c>, the audit-log cursor columns say catch-up and backlog, and the
    /// JSON keys inside stored documents follow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Hand-written, because EF's generated diff would have destroyed data.</strong> It
    /// could not see <c>modbot_rollup_daily → modbot_daily_total</c> as a rename once the primary
    /// key's name changed with it, so it emitted <c>DropTable</c> + <c>CreateTable</c> — every
    /// daily total gone. And it cannot express a three-column rotation where one column's new name
    /// is another column's old name. Everything here is a rename; no row is touched except to
    /// rename a JSON key inside it.
    /// </para>
    /// <para>
    /// Order matters in the settings block. The existing <c>audit_log_catch_up_offset</c> was the
    /// tail poll's <em>backlog</em> cursor; it has to move to <c>audit_log_backlog_offset</c>
    /// before the old <c>audit_log_backfill_offset</c> can take the catch-up name.
    /// </para>
    /// </remarks>
    public partial class PlainNamesInSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // settings: three-column rotation, collision-safe order.
            migrationBuilder.RenameColumn(
                name: "audit_log_catch_up_offset", table: "settings", newName: "audit_log_backlog_offset");
            migrationBuilder.RenameColumn(
                name: "audit_log_backfill_offset", table: "settings", newName: "audit_log_catch_up_offset");
            migrationBuilder.RenameColumn(
                name: "audit_log_backfill_complete", table: "settings", newName: "audit_log_catch_up_complete");

            // daily totals: table, primary key, index. RenameTable does not rename constraints.
            migrationBuilder.RenameTable(name: "modbot_rollup_daily", newName: "modbot_daily_total");
            migrationBuilder.Sql(
                "ALTER TABLE modbot_daily_total RENAME CONSTRAINT pk_modbot_rollup_daily TO pk_modbot_daily_total;");
            migrationBuilder.RenameIndex(
                name: "ix_modbot_rollup_daily_metric", table: "modbot_daily_total", newName: "ix_modbot_daily_total_metric");

            // daily totals state: table, primary key, check constraint.
            migrationBuilder.RenameTable(name: "modbot_rollup_state", newName: "modbot_daily_totals_state");
            migrationBuilder.Sql(
                "ALTER TABLE modbot_daily_totals_state RENAME CONSTRAINT pk_modbot_rollup_state TO pk_modbot_daily_totals_state;");
            migrationBuilder.Sql(
                "ALTER TABLE modbot_daily_totals_state RENAME CONSTRAINT ck_modbot_rollup_state_singleton TO ck_modbot_daily_totals_state_singleton;");

            // JSON keys inside stored documents. Only rows carrying the old key are touched, so on
            // a fresh deployment these are no-ops.
            migrationBuilder.Sql(RenameJsonKey("settings", "sync_pacing", "auditLogBackfill", "auditLogCatchUp"));
            migrationBuilder.Sql(RenameJsonKey("settings", "sync_pacing", "auditLogMaxBackfillPages", "auditLogMaxCatchUpPages"));
            migrationBuilder.Sql(RenameJsonKey("modbot_event", "data", "countedRollups", "countedDailyTotals"));
            migrationBuilder.Sql(RenameJsonKey("modbot_event", "data", "evacuated", "movedOut"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RenameJsonKey("modbot_event", "data", "movedOut", "evacuated"));
            migrationBuilder.Sql(RenameJsonKey("modbot_event", "data", "countedDailyTotals", "countedRollups"));
            migrationBuilder.Sql(RenameJsonKey("settings", "sync_pacing", "auditLogMaxCatchUpPages", "auditLogMaxBackfillPages"));
            migrationBuilder.Sql(RenameJsonKey("settings", "sync_pacing", "auditLogCatchUp", "auditLogBackfill"));

            migrationBuilder.Sql(
                "ALTER TABLE modbot_daily_totals_state RENAME CONSTRAINT ck_modbot_daily_totals_state_singleton TO ck_modbot_rollup_state_singleton;");
            migrationBuilder.Sql(
                "ALTER TABLE modbot_daily_totals_state RENAME CONSTRAINT pk_modbot_daily_totals_state TO pk_modbot_rollup_state;");
            migrationBuilder.RenameTable(name: "modbot_daily_totals_state", newName: "modbot_rollup_state");

            migrationBuilder.RenameIndex(
                name: "ix_modbot_daily_total_metric", table: "modbot_daily_total", newName: "ix_modbot_rollup_daily_metric");
            migrationBuilder.Sql(
                "ALTER TABLE modbot_daily_total RENAME CONSTRAINT pk_modbot_daily_total TO pk_modbot_rollup_daily;");
            migrationBuilder.RenameTable(name: "modbot_daily_total", newName: "modbot_rollup_daily");

            // reverse rotation, collision-safe order
            migrationBuilder.RenameColumn(
                name: "audit_log_catch_up_complete", table: "settings", newName: "audit_log_backfill_complete");
            migrationBuilder.RenameColumn(
                name: "audit_log_catch_up_offset", table: "settings", newName: "audit_log_backfill_offset");
            migrationBuilder.RenameColumn(
                name: "audit_log_backlog_offset", table: "settings", newName: "audit_log_catch_up_offset");
        }

        /// <summary>
        /// Moves one key inside a <c>jsonb</c> column, leaving every other key where it was.
        /// </summary>
        /// <remarks>
        /// Column and key names are this file's own constants, never input, which is what makes
        /// building the statement by concatenation acceptable here and nowhere else.
        /// </remarks>
        private static string RenameJsonKey(string table, string column, string oldKey, string newKey) =>
            $"UPDATE {table} SET {column} = ({column} - '{oldKey}') || jsonb_build_object('{newKey}', {column}->'{oldKey}') "
            + $"WHERE {column} ? '{oldKey}';";
    }
}
