using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The <c>group_member</c> and <c>group_ban</c> tables (member and ban sync design §2) and
    /// the two sweeps' cursors on the settings row (§3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-checked against the design: every id is <c>text</c> with no length (foundation
    /// §3.1.1); the key is <c>(group_id, user_id)</c> so the tables do not assume one group
    /// forever; <c>roles</c>, <c>waiting_facts</c> and <c>raw</c> are <c>jsonb</c>; VRChat's own
    /// words for status and visibility are short text, never an enum; and nothing here is ever
    /// deleted -- <c>left_at</c> and <c>lifted_at</c> are how a row goes away.
    /// </para>
    /// <para>
    /// Four indexes each way. The current-members index is what the Members page reads, newest
    /// joiner first; the user index is the subject pane; the GIN index on <c>roles</c> answers the
    /// role filter's containment test; and the partial index on <c>waiting_facts IS NOT NULL</c>
    /// covers the end-of-sweep question "which rows still have a change to record", which almost
    /// no row ever does.
    /// </para>
    /// </remarks>
    public partial class AddGroupMembersAndBans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ban_sweep_completed_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ban_sweep_count",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ban_sweep_offset",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ban_sweep_polled_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ban_sweep_previous_started_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ban_sweep_seen_so_far",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ban_sweep_started_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "member_sweep_completed_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "member_sweep_count",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "member_sweep_offset",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "member_sweep_polled_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "member_sweep_previous_started_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "member_sweep_seen_so_far",
                table: "settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "member_sweep_started_at",
                table: "settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "group_ban",
                columns: table => new
                {
                    group_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    banned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lifted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    waiting_facts = table.Column<string>(type: "jsonb", nullable: true),
                    raw = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_ban", x => new { x.group_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "group_member",
                columns: table => new
                {
                    group_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    membership_id = table.Column<string>(type: "text", nullable: true),
                    roles = table.Column<string>(type: "jsonb", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    membership_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    visibility = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    is_representing = table.Column<bool>(type: "boolean", nullable: false),
                    manager_notes = table.Column<string>(type: "text", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    left_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    waiting_facts = table.Column<string>(type: "jsonb", nullable: true),
                    raw = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_member", x => new { x.group_id, x.user_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_ban_current",
                table: "group_ban",
                columns: new[] { "group_id", "lifted_at", "banned_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_group_ban_user",
                table: "group_ban",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_group_ban_waiting",
                table: "group_ban",
                column: "group_id",
                filter: "waiting_facts IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_group_member_current",
                table: "group_member",
                columns: new[] { "group_id", "left_at", "joined_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_group_member_roles",
                table: "group_member",
                column: "roles")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_group_member_user",
                table: "group_member",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_group_member_waiting",
                table: "group_member",
                column: "group_id",
                filter: "waiting_facts IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_ban");

            migrationBuilder.DropTable(
                name: "group_member");

            migrationBuilder.DropColumn(
                name: "ban_sweep_completed_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ban_sweep_count",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ban_sweep_offset",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ban_sweep_polled_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ban_sweep_previous_started_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ban_sweep_seen_so_far",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "ban_sweep_started_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "member_sweep_completed_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "member_sweep_count",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "member_sweep_offset",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "member_sweep_polled_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "member_sweep_previous_started_at",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "member_sweep_seen_so_far",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "member_sweep_started_at",
                table: "settings");
        }
    }
}
