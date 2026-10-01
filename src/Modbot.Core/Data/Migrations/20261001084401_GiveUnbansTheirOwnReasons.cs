using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Gives each reason the actions it is offered on, and gives unbans reasons of their own (M4 §9,
    /// ban case files open question 2); and lets a case file record that its ban was lifted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every reason already on a list keeps the actions it meant something on -- ban, kick and
    /// turning down a join request -- and stops being offered on an unban, where "Harassment" is
    /// no answer to "why lift the ban?".
    /// </para>
    /// <para>
    /// A list that already exists gets the unban reasons added to its end, so the unban
    /// confirmation has buttons on the day this ships. A list that does not exist yet is seeded on
    /// first read with them included, as before. A fixed time, as the built-in roles were given:
    /// this is a seed, not an event.
    /// </para>
    /// </remarks>
    public partial class GiveUnbansTheirOwnReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "lift_note",
                table: "case_file",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lift_reason_ids",
                table: "case_file",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lifted_at",
                table: "case_file",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lifted_by_user_id",
                table: "case_file",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lifted_by_username",
                table: "case_file",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "unban_fact_id",
                table: "case_file",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "used_for",
                table: "ban_reason",
                type: "integer",
                nullable: false,
                defaultValue: ReasonUseBanKickReject);

            migrationBuilder.Sql(
                "INSERT INTO ban_reason (id, label, description, sort_order, is_active, needs_written_reason, used_for, created_at, updated_at) "
                + "SELECT gen_random_uuid(), v.label, v.description, (SELECT max(sort_order) FROM ban_reason) + v.n, true, v.needs, "
                + $"{ReasonUseUnban}, '2026-10-01T00:00:00Z', '2026-10-01T00:00:00Z' "
                + "FROM (VALUES "
                + "(1, 'Mistake', 'The ban should not have happened.', false), "
                + "(2, 'Appeal upheld', 'They asked to come back and the team agreed.', false), "
                + "(3, 'Time served', 'The ban was only ever meant to last this long.', false), "
                + "(4, 'Other', 'None of the above. Say why in the note.', true)"
                + ") AS v(n, label, description, needs) "
                + "WHERE EXISTS (SELECT 1 FROM ban_reason);");
        }

        // ReasonUse as stored. Written out rather than read from the enum, so a later change to it
        // cannot change what this migration did.
        private const int ReasonUseBanKickReject = 1 | 2 | 8;
        private const int ReasonUseUnban = 4;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reasons are never deleted (case files cite them by id). The unban-only ones are
            // switched off instead, so they do not land on the ban buttons once the column is gone.
            migrationBuilder.Sql($"UPDATE ban_reason SET is_active = false WHERE used_for = {ReasonUseUnban};");

            migrationBuilder.DropColumn(
                name: "lift_note",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "lift_reason_ids",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "lifted_at",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "lifted_by_user_id",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "lifted_by_username",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "unban_fact_id",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "used_for",
                table: "ban_reason");
        }
    }
}
