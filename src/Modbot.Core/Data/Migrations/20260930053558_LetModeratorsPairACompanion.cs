using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Gives the new "Pair a companion" permission to the roles that need it on the day it starts
    /// being checked. Data only: the schema does not change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// From this version a companion works only while the account it is paired to holds Pair a
    /// companion, and making a pairing code needs it too. Without this migration every companion
    /// paired to anybody but an administrator would stop at the first request after the update.
    /// </para>
    /// <para>
    /// <strong>Up.</strong> The bit is added to the built-in Moderator role, and to every role
    /// held by an account that owns a companion that is not revoked. That second part is the
    /// maintainer's decision (2026-09-30): a group whose moderators hold a role of its own rather
    /// than the built-in one keeps its companions working through the update, and an operator who
    /// does not want that role to pair takes the box off afterwards. Administrator is left alone;
    /// it holds everything already, including permissions added later.
    /// </para>
    /// <para>
    /// <strong>Down.</strong> The bit is taken off every role. A role somebody gave it to by hand
    /// after the update loses it too; the older version does not know the bit and ignores it, so
    /// nothing else changes.
    /// </para>
    /// </remarks>
    public partial class LetModeratorsPairACompanion : Migration
    {
        /// <summary><c>ModbotPermissions.PairCompanion</c>. Written out, so this migration never changes meaning.</summary>
        private const long PairCompanion = 1L << 43;

        private const string AdministratorRole = "00000000-0000-0000-0000-000000000001";
        private const string ModeratorRole = "00000000-0000-0000-0000-000000000002";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"UPDATE modbot_role SET permissions = permissions | {PairCompanion} "
                + $"WHERE id <> '{AdministratorRole}' "
                + $"AND (id = '{ModeratorRole}' "
                + "OR id IN (SELECT ur.role_id FROM modbot_user_role ur "
                + "JOIN companion_device d ON d.issued_to_user_id = ur.user_id "
                + "WHERE d.revoked_at IS NULL));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"UPDATE modbot_role SET permissions = permissions & ~{PairCompanion};");
        }
    }
}
