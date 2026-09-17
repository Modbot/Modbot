using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Adds <c>group_member_count</c> -- one reading of the group's member count and online
    /// member count per group-info poll -- and fills it from the readings the fact log already
    /// holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The catch-up reads every <c>vrchat.group.update</c> fact that carried a count: the baseline
    /// carries both, a change carries only the fields that moved. A change to one count says
    /// nothing about the other, so each count is carried forward from the last fact that stated
    /// it (the two <c>count() OVER</c> columns number the runs between stated values, and
    /// <c>first_value</c> within a run is the value stated at its start). A fact from before any
    /// member count was stated makes no reading; an online count never stated reads as zero.
    /// </para>
    /// <para>
    /// The reading's time is when the poll saw it: <c>occurred_before</c> for a change, whose
    /// <c>occurred_at</c> is the previous poll, and <c>occurred_at</c> for the baseline. Audit-log
    /// facts of the same type carry neither key and make no reading.
    /// </para>
    /// <para>
    /// Hand-edited after scaffolding to add the catch-up; the table is what EF generated.
    /// </para>
    /// </remarks>
    public partial class AddGroupMemberCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "group_member_count",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    group_id = table.Column<string>(type: "text", nullable: false),
                    counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    member_count = table.Column<int>(type: "integer", nullable: false),
                    online_member_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_member_count", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_member_count_group_time",
                table: "group_member_count",
                columns: new[] { "group_id", "counted_at" });

            migrationBuilder.Sql(
                """
                INSERT INTO group_member_count (group_id, counted_at, member_count, online_member_count)
                SELECT c.group_id, c.at, c.members, COALESCE(c.online, 0)
                FROM (
                    SELECT f.group_id, f.at, f.id,
                           first_value(f.members) OVER (PARTITION BY f.group_id, f.members_run ORDER BY f.at, f.id) AS members,
                           first_value(f.online) OVER (PARTITION BY f.group_id, f.online_run ORDER BY f.at, f.id) AS online
                    FROM (
                        SELECT r.group_id, r.at, r.id, r.members, r.online,
                               count(r.members) OVER (PARTITION BY r.group_id ORDER BY r.at, r.id) AS members_run,
                               count(r.online) OVER (PARTITION BY r.group_id ORDER BY r.at, r.id) AS online_run
                        FROM (
                            SELECT e.subject_id AS group_id,
                                   COALESCE(e.occurred_before, e.occurred_at) AS at,
                                   e.id,
                                   COALESCE(e.data->'changed'->'MemberCount'->>'new', e.data->'baseline'->>'MemberCount')::int AS members,
                                   COALESCE(e.data->'changed'->'OnlineMemberCount'->>'new', e.data->'baseline'->>'OnlineMemberCount')::int AS online
                            FROM modbot_event e
                            WHERE e.type = 'vrchat.group.update'
                        ) r
                        WHERE r.members IS NOT NULL OR r.online IS NOT NULL
                    ) f
                ) c
                WHERE c.members IS NOT NULL
                ORDER BY c.at, c.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_member_count");
        }
    }
}
