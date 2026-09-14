using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The <c>ban_reason</c> and <c>case_file</c> tables (ban case files design §2 and §3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-checked against the design: every VRChat id is <c>text</c> with no length (foundation
    /// §3.1.1); the reason ids and the three snapshot columns are <c>jsonb</c>; the written reason
    /// is unbounded <c>text</c> because it is a moderator's own account and the API, not the
    /// schema, decides how long is too long; and nothing here is ever deleted -- a case file is
    /// withdrawn with <c>withdrawn_at</c>, a reason is switched off with <c>is_active</c>.
    /// </para>
    /// <para>
    /// Three indexes on <c>case_file</c>. By person, newest first, for the subject pane and the
    /// badges on the ban lists; by audit entry id, partial, for "which case file is about this
    /// ban"; and by created date for the list page. The ban reasons are read in sort order every
    /// time a ban is written up. An empty <c>descending</c> array is EF's spelling of "every
    /// column descending".
    /// </para>
    /// </remarks>
    public partial class AddCaseFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ban_reason",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    needs_written_reason = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ban_reason", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "case_file",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    group_id = table.Column<string>(type: "text", nullable: true),
                    audit_entry_id = table.Column<string>(type: "text", nullable: true),
                    ban_fact_id = table.Column<long>(type: "bigint", nullable: true),
                    banned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason_ids = table.Column<string>(type: "jsonb", nullable: false),
                    written_reason = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    withdrawn_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    withdrawn_by_username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    withdrawn_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    profile_at_ban = table.Column<string>(type: "jsonb", nullable: true),
                    membership_at_ban = table.Column<string>(type: "jsonb", nullable: true),
                    ban_list_entry_at_ban = table.Column<string>(type: "jsonb", nullable: true),
                    snapshot_taken_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    profile_refreshed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    snapshot_recaptured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_file", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ban_reason_order",
                table: "ban_reason",
                column: "sort_order");

            migrationBuilder.CreateIndex(
                name: "ix_case_file_audit_entry",
                table: "case_file",
                column: "audit_entry_id",
                filter: "audit_entry_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_case_file_created",
                table: "case_file",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_case_file_user",
                table: "case_file",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ban_reason");

            migrationBuilder.DropTable(
                name: "case_file");
        }
    }
}
