using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Adds <c>vrchat_user.trust_rank</c> -- the rank the stored tag list says, kept as a column so
    /// every list that shows a person can carry it without parsing <c>tags</c> per row -- and
    /// fills it for everyone whose tags are already stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The catch-up is the same rule as <c>TrustRanks.FromTags</c>, written once in SQL: staff
    /// beats everything, either nuisance tag beats the ladder, then the highest ladder tag. The
    /// numbers are the enum's and are never renumbered. Rows with no tags stay null, which means
    /// "not read yet", not Visitor.
    /// </para>
    /// <para>
    /// Hand-edited after scaffolding to add the catch-up; the column itself is what EF generated.
    /// </para>
    /// </remarks>
    public partial class AddTrustRank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "trust_rank",
                table: "vrchat_user",
                type: "smallint",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE vrchat_user SET trust_rank = CASE
                    WHEN tags @> '["admin_moderator"]' THEN 7
                    WHEN tags @> '["system_troll"]' OR tags @> '["system_probable_troll"]' THEN 6
                    WHEN tags @> '["system_trust_legend"]' THEN 5
                    WHEN tags @> '["system_trust_veteran"]' THEN 4
                    WHEN tags @> '["system_trust_trusted"]' THEN 3
                    WHEN tags @> '["system_trust_known"]' THEN 2
                    WHEN tags @> '["system_trust_basic"]' THEN 1
                    ELSE 0 END
                WHERE tags IS NOT NULL AND jsonb_typeof(tags) = 'array';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trust_rank",
                table: "vrchat_user");
        }
    }
}
