using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// The head count becomes the instance page's <c>userCount</c> instead of <c>n_users</c>, and the
    /// readings already stored are put right the same way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a live group a club read <c>n_users</c> 80 while <c>userCount</c> said 51 and the instance
    /// held about 52; over one evening's 281 page readings the two differed 229 times
    /// (<c>HeadCounts</c>). Every page reading already kept <c>userCount</c> beside <c>n_users</c>, so
    /// the right number is in the table and only has to be moved into <c>head_count</c>.
    /// </para>
    /// <para>
    /// <strong>Up.</strong> Every page reading keeps its old number in the new <c>n_users</c> column and
    /// takes <c>user_count</c> as its head count; one without a <c>user_count</c> keeps <c>n_users</c>
    /// and is unsure from now on. An instance whose latest reading is a page reading and whose current
    /// count still matches it takes the corrected number. Every instance with readings has its peak
    /// worked out again as its highest reading, unsure when only unsure readings reach it; an instance
    /// with no readings keeps its peak.
    /// </para>
    /// <para>
    /// <strong>Down.</strong> The same in reverse: the current count and every reading go back to
    /// <c>n_users</c> wherever it was kept, and the peaks are worked out again from the readings. A
    /// peak is not stored anywhere else, so it comes back as the highest reading rather than as the
    /// exact number it was, which can differ where the group list's member count once raised it.
    /// </para>
    /// <para>
    /// <c>instance_head_count</c> is an ordinary table, not a partitioned one. The readings are one pass
    /// over it; the latest reading of each instance is one lookup on
    /// <c>(instance_id, counted_at)</c>; the peaks are one grouped pass.
    /// </para>
    /// </remarks>
    public partial class ReadTheHeadCountFromUserCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "head_count_unsure",
                table: "vrchat_instance",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "peak_unsure",
                table: "vrchat_instance",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "n_users",
                table: "instance_head_count",
                type: "integer",
                nullable: true);

            // Every page reading's head count was n_users. It is kept as n_users, and the head count
            // becomes userCount wherever the reading has one.
            migrationBuilder.Sql("""
                UPDATE instance_head_count
                SET n_users = head_count,
                    head_count = COALESCE(user_count, head_count)
                WHERE source = 'page';
                """);

            // The count an instance shows now: its latest reading, where that is a page reading and the
            // instance's count still matches the old number (a list read since may have replaced it).
            migrationBuilder.Sql("""
                WITH latest AS (
                    SELECT i.id, l.head_count, l.user_count, l.n_users
                    FROM vrchat_instance i
                    CROSS JOIN LATERAL (
                        SELECT h.head_count, h.user_count, h.n_users
                        FROM instance_head_count h
                        WHERE h.instance_id = i.id
                        ORDER BY h.counted_at DESC, h.id DESC
                        LIMIT 1
                    ) l
                    WHERE i.head_count_source = 'page'
                )
                UPDATE vrchat_instance v
                SET head_count = l.head_count,
                    head_count_unsure = (l.user_count IS NULL)
                FROM latest l
                WHERE v.id = l.id
                  AND l.n_users IS NOT NULL
                  AND v.head_count = l.n_users;
                """);

            migrationBuilder.Sql(Peaks);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Before the readings change back, while an instance's count can still be matched to its
            // latest reading.
            migrationBuilder.Sql("""
                WITH latest AS (
                    SELECT i.id, l.head_count, l.n_users
                    FROM vrchat_instance i
                    CROSS JOIN LATERAL (
                        SELECT h.head_count, h.n_users
                        FROM instance_head_count h
                        WHERE h.instance_id = i.id
                        ORDER BY h.counted_at DESC, h.id DESC
                        LIMIT 1
                    ) l
                    WHERE i.head_count_source = 'page'
                )
                UPDATE vrchat_instance v
                SET head_count = l.n_users
                FROM latest l
                WHERE v.id = l.id
                  AND l.n_users IS NOT NULL
                  AND v.head_count = l.head_count;
                """);

            migrationBuilder.Sql("""
                UPDATE instance_head_count
                SET head_count = n_users
                WHERE n_users IS NOT NULL;
                """);

            migrationBuilder.Sql(Peaks);

            migrationBuilder.DropColumn(
                name: "head_count_unsure",
                table: "vrchat_instance");

            migrationBuilder.DropColumn(
                name: "peak_unsure",
                table: "vrchat_instance");

            migrationBuilder.DropColumn(
                name: "n_users",
                table: "instance_head_count");
        }

        /// <summary>
        /// Each instance's peak as its highest reading, and unsure when no sure reading reaches it. A
        /// reading is unsure when it came from the page with no <c>user_count</c>. Instances with no
        /// readings are left alone. Going down, the column is still there and is dropped just after.
        /// </summary>
        private const string Peaks = """
            UPDATE vrchat_instance v
            SET peak_user_count = p.most,
                peak_unsure = (p.sure_most IS NULL OR p.sure_most < p.most)
            FROM (
                SELECT h.instance_id,
                       MAX(h.head_count) AS most,
                       MAX(h.head_count) FILTER (
                           WHERE NOT (h.source = 'page' AND h.user_count IS NULL)) AS sure_most
                FROM instance_head_count h
                GROUP BY h.instance_id
            ) p
            WHERE v.id = p.instance_id;
            """;
    }
}
