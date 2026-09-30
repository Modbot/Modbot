using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Gives roles an order, and puts the roles that exist into a first one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Manage users and Manage roles only reach accounts and roles below the caller's highest role
    /// (accounts and access design §3.5), so every role needs a place in a line. A smaller
    /// <c>position</c> is a higher role.
    /// </para>
    /// <para>
    /// <strong>Up.</strong> Administrator first. Then Moderator and every custom role, the one that
    /// allows the most first and older first between equals. Viewer last of all. That is only a
    /// starting point, chosen so that nobody's reach changes on the day of the update by more than
    /// the new rule itself: the operator reorders from the roles page afterwards.
    /// </para>
    /// <para>
    /// <strong>Down.</strong> The column goes; the older version has no order.
    /// </para>
    /// </remarks>
    public partial class GiveRolesAnOrder : Migration
    {
        // Written out, so this migration never changes meaning (the ids are fixed: BuiltInRoles).
        private const string AdministratorRole = "00000000-0000-0000-0000-000000000001";
        private const string ViewerRole = "00000000-0000-0000-0000-000000000003";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "position",
                table: "modbot_role",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // The number of permissions is the number of 1s in the bit string, counted as the
            // characters left after the 0s are taken out.
            migrationBuilder.Sql(
                "UPDATE modbot_role AS r SET position = o.place "
                + "FROM ("
                + "SELECT id, ROW_NUMBER() OVER (ORDER BY "
                + $"CASE id WHEN '{AdministratorRole}' THEN 0 WHEN '{ViewerRole}' THEN 2 ELSE 1 END, "
                + "length(replace((permissions::bit(64))::text, '0', '')) DESC, "
                + "created_at, id) - 1 AS place "
                + "FROM modbot_role) AS o "
                + "WHERE r.id = o.id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "position",
                table: "modbot_role");
        }
    }
}
