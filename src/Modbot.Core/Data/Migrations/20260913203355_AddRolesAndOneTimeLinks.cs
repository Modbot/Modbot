using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Roles, the user-role link, one-time links, the public address setting, and the account
    /// columns the accounts and access design adds: the session cut-off, an email address, and
    /// the linked VRChat account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Hand-ordered, because the generated version dropped <c>modbot_user.permissions</c>
    /// first.</strong> That column is every existing account's access, and it must be carried into
    /// roles before it goes: an account holding the Administrator bit gets the Administrator role;
    /// an account with any other non-zero value gets its own <c>Carried over: name</c> role holding
    /// exactly that value, so nothing anybody was allowed to do changes (design §3.3). Only then is
    /// the column dropped. <c>Down</c> rebuilds it from the roles' union.
    /// </para>
    /// <para>
    /// The built-in roles' values are written as numbers rather than read from
    /// <c>BuiltInRoles</c>, so this file keeps meaning what it meant when it ran. The rows are
    /// editable afterwards; the constants describe the starting point, not a contract.
    /// </para>
    /// <para>
    /// Every existing account comes out of this migration <em>unlinked</em>. The design makes the
    /// VRChat link a requirement, so the first sign-in after upgrading lands on the link page.
    /// </para>
    /// </remarks>
    public partial class AddRolesAndOneTimeLinks : Migration
    {
        /// <summary>1L &lt;&lt; 62. Checked as a flag rather than expanded (foundation §7.3).</summary>
        private const long Administrator = 4611686018427387904L;

        /// <summary>
        /// ViewMembers | ViewProfile | ViewAnalytics | ViewAuditLog | Kick | Ban | Unban | Warn |
        /// ViewEvidence | UploadEvidence.
        /// </summary>
        private const long Moderator = 1L | 2L | 4L | 8L | 256L | 512L | 1024L | 2048L | 32768L | 65536L;

        /// <summary>ViewMembers | ViewProfile | ViewAnalytics | ViewAuditLog.</summary>
        private const long Viewer = 1L | 2L | 4L | 8L;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "modbot_role",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name_normalized = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    permissions = table.Column<long>(type: "bigint", nullable: false),
                    is_built_in = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_role", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_role_name_normalized",
                table: "modbot_role",
                column: "name_normalized",
                unique: true);

            migrationBuilder.CreateTable(
                name: "modbot_user_role",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_user_role", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_modbot_user_role_modbot_role_role_id",
                        column: x => x.role_id,
                        principalTable: "modbot_role",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_modbot_user_role_modbot_user_user_id",
                        column: x => x.user_id,
                        principalTable: "modbot_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_user_role_role_id",
                table: "modbot_user_role",
                column: "role_id");

            migrationBuilder.CreateTable(
                name: "modbot_one_time_link",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    role_ids = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modbot_one_time_link", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_modbot_one_time_link_created_by_user_id",
                table: "modbot_one_time_link",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_modbot_one_time_link_token_hash",
                table: "modbot_one_time_link",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_modbot_one_time_link_user_id",
                table: "modbot_one_time_link",
                column: "user_id");

            // The three built-in roles. Fixed ids, matching BuiltInRoles; a fixed created_at
            // rather than now(), because this is a seed and not an event.
            migrationBuilder.Sql(
                "INSERT INTO modbot_role (id, name, name_normalized, description, permissions, is_built_in, created_at) VALUES "
                + $"('00000000-0000-0000-0000-000000000001', 'Administrator', 'ADMINISTRATOR', 'Can do everything, including things added in future versions.', {Administrator}, true, '2026-09-13T00:00:00Z'), "
                + $"('00000000-0000-0000-0000-000000000002', 'Moderator', 'MODERATOR', 'Kick, ban, warn and unban; attach and view evidence; read the audit log.', {Moderator}, true, '2026-09-13T00:00:00Z'), "
                + $"('00000000-0000-0000-0000-000000000003', 'Viewer', 'VIEWER', 'Can look at members, history and analytics, and change nothing.', {Viewer}, true, '2026-09-13T00:00:00Z');");

            // Carry existing access into roles before the column goes.
            migrationBuilder.Sql(
                "INSERT INTO modbot_user_role (user_id, role_id) "
                + "SELECT id, '00000000-0000-0000-0000-000000000001' FROM modbot_user "
                + $"WHERE (permissions & {Administrator}) <> 0;");

            // Anything else non-zero becomes that account's own role, holding exactly what it
            // held. The name is truncated to fit; a collision between two truncated names would
            // fail the unique index and therefore the migration, which is the right outcome for a
            // case that should never occur.
            migrationBuilder.Sql(
                "INSERT INTO modbot_role (id, name, name_normalized, description, permissions, is_built_in, created_at) "
                + "SELECT gen_random_uuid(), 'Carried over: ' || left(username, 50), upper('Carried over: ' || left(username, 50)), "
                + "'The permissions this account had before roles existed.', permissions, false, created_at "
                + $"FROM modbot_user WHERE (permissions & {Administrator}) = 0 AND permissions <> 0;");

            migrationBuilder.Sql(
                "INSERT INTO modbot_user_role (user_id, role_id) "
                + "SELECT u.id, r.id FROM modbot_user u "
                + "JOIN modbot_role r ON r.name_normalized = upper('Carried over: ' || left(u.username, 50)) "
                + $"WHERE (u.permissions & {Administrator}) = 0 AND u.permissions <> 0;");

            migrationBuilder.DropColumn(
                name: "permissions",
                table: "modbot_user");

            // The public address people reach this Modbot at (design §4.2). Null until a person
            // saves one; nothing is sent out of band until then.
            migrationBuilder.AddColumn<string>(
                name: "public_address",
                table: "settings",
                type: "text",
                nullable: true);

            // The new account columns. All nullable or defaulted: existing rows need nothing.
            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "modbot_user",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sessions_valid_after",
                table: "modbot_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vr_chat_display_name",
                table: "modbot_user",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "vr_chat_link_checks",
                table: "modbot_user",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "vr_chat_link_code",
                table: "modbot_user",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "vr_chat_link_code_expires_at",
                table: "modbot_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "vr_chat_link_last_check_at",
                table: "modbot_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vr_chat_link_pending_user_id",
                table: "modbot_user",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "vr_chat_linked_at",
                table: "modbot_user",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vr_chat_user_id",
                table: "modbot_user",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_modbot_user_vr_chat_user_id",
                table: "modbot_user",
                column: "vr_chat_user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_modbot_user_vr_chat_user_id",
                table: "modbot_user");

            migrationBuilder.DropColumn(name: "public_address", table: "settings");

            migrationBuilder.DropColumn(name: "email", table: "modbot_user");
            migrationBuilder.DropColumn(name: "sessions_valid_after", table: "modbot_user");
            migrationBuilder.DropColumn(name: "vr_chat_display_name", table: "modbot_user");
            migrationBuilder.DropColumn(name: "vr_chat_link_checks", table: "modbot_user");
            migrationBuilder.DropColumn(name: "vr_chat_link_code", table: "modbot_user");
            migrationBuilder.DropColumn(name: "vr_chat_link_code_expires_at", table: "modbot_user");
            migrationBuilder.DropColumn(name: "vr_chat_link_last_check_at", table: "modbot_user");
            migrationBuilder.DropColumn(name: "vr_chat_link_pending_user_id", table: "modbot_user");
            migrationBuilder.DropColumn(name: "vr_chat_linked_at", table: "modbot_user");
            migrationBuilder.DropColumn(name: "vr_chat_user_id", table: "modbot_user");

            migrationBuilder.AddColumn<long>(
                name: "permissions",
                table: "modbot_user",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Back to a bitfield per account: the union of the roles, exactly as the running code
            // computed it.
            migrationBuilder.Sql(
                "UPDATE modbot_user u SET permissions = COALESCE("
                + "(SELECT bit_or(r.permissions) FROM modbot_user_role ur JOIN modbot_role r ON r.id = ur.role_id WHERE ur.user_id = u.id), 0);");

            migrationBuilder.DropTable(
                name: "modbot_one_time_link");

            migrationBuilder.DropTable(
                name: "modbot_user_role");

            migrationBuilder.DropTable(
                name: "modbot_role");
        }
    }
}
