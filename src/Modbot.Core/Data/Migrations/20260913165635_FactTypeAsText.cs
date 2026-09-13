using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class FactTypeAsText : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// EF's generated AlterColumn has no USING clause, and PostgreSQL will not cast smallint to
        /// text implicitly, so this is hand-written. The CASE is the old enum's numbering, which
        /// was the one thing that file said never to change -- it is preserved here, in the only
        /// place it still matters. On a partitioned table the ALTER cascades to every partition.
        ///
        /// A code this list does not know becomes modbot.unrecognised with the number kept in
        /// type_raw, rather than failing the migration or inventing a name. Nothing is dropped.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "type_raw",
                table: "modbot_event",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE modbot_event SET type_raw = type::text
                WHERE type NOT IN (100, 101, 102, 103, 104, 105, 106, 107, 108, 200, 201, 202, 203, 300, 301, 302, 303, 304, 305, 400, 401, 402, 403, 404, 405, 500, 501, 502, 503, 504, 505, 506);
                """);

            migrationBuilder.Sql(
                $"""
                ALTER TABLE modbot_event
                    ALTER COLUMN type TYPE character varying(128)
                    USING (CASE type
                        WHEN 100 THEN 'vrchat.group.member.join'
                        WHEN 101 THEN 'vrchat.group.member.leave'
                        WHEN 102 THEN 'vrchat.group.member.ban'
                        WHEN 103 THEN 'vrchat.group.member.unban'
                        WHEN 104 THEN 'vrchat.group.member.remove'
                        WHEN 105 THEN 'vrchat.group.role.assign'
                        WHEN 106 THEN 'vrchat.group.role.unassign'
                        WHEN 107 THEN 'vrchat.group.invite.create'
                        WHEN 108 THEN 'vrchat.group.update'
                        WHEN 200 THEN 'vrchat.instance.join'
                        WHEN 201 THEN 'vrchat.instance.leave'
                        WHEN 202 THEN 'vrchat.avatar.change'
                        WHEN 203 THEN 'vrchat.instance.presence'
                        WHEN 300 THEN 'discord.member.join'
                        WHEN 301 THEN 'discord.member.leave'
                        WHEN 302 THEN 'discord.voice.join'
                        WHEN 303 THEN 'discord.voice.leave'
                        WHEN 304 THEN 'discord.role.assign'
                        WHEN 305 THEN 'discord.role.unassign'
                        WHEN 400 THEN 'modbot.auth.login'
                        WHEN 401 THEN 'modbot.auth.login.failed'
                        WHEN 402 THEN 'modbot.auth.password.change'
                        WHEN 403 THEN 'modbot.apikey.create'
                        WHEN 404 THEN 'modbot.apikey.revoke'
                        WHEN 405 THEN 'modbot.settings.change'
                        WHEN 500 THEN 'modbot.sync.failed'
                        WHEN 501 THEN 'modbot.ratelimit.coldstop'
                        WHEN 502 THEN 'modbot.waf.blocked'
                        WHEN 503 THEN 'modbot.migration.applied'
                        WHEN 504 THEN 'modbot.retention.pruned'
                        WHEN 505 THEN 'modbot.partition.created'
                        WHEN 506 THEN 'modbot.user.purged'
                        ELSE 'modbot.unrecognised'
                    END);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                $"""
                ALTER TABLE modbot_event
                    ALTER COLUMN type TYPE smallint
                    USING (CASE type
                        WHEN 'vrchat.group.member.join' THEN 100
                        WHEN 'vrchat.group.member.leave' THEN 101
                        WHEN 'vrchat.group.member.ban' THEN 102
                        WHEN 'vrchat.group.member.unban' THEN 103
                        WHEN 'vrchat.group.member.remove' THEN 104
                        WHEN 'vrchat.group.role.assign' THEN 105
                        WHEN 'vrchat.group.role.unassign' THEN 106
                        WHEN 'vrchat.group.invite.create' THEN 107
                        WHEN 'vrchat.group.update' THEN 108
                        WHEN 'vrchat.instance.join' THEN 200
                        WHEN 'vrchat.instance.leave' THEN 201
                        WHEN 'vrchat.avatar.change' THEN 202
                        WHEN 'vrchat.instance.presence' THEN 203
                        WHEN 'discord.member.join' THEN 300
                        WHEN 'discord.member.leave' THEN 301
                        WHEN 'discord.voice.join' THEN 302
                        WHEN 'discord.voice.leave' THEN 303
                        WHEN 'discord.role.assign' THEN 304
                        WHEN 'discord.role.unassign' THEN 305
                        WHEN 'modbot.auth.login' THEN 400
                        WHEN 'modbot.auth.login.failed' THEN 401
                        WHEN 'modbot.auth.password.change' THEN 402
                        WHEN 'modbot.apikey.create' THEN 403
                        WHEN 'modbot.apikey.revoke' THEN 404
                        WHEN 'modbot.settings.change' THEN 405
                        WHEN 'modbot.sync.failed' THEN 500
                        WHEN 'modbot.ratelimit.coldstop' THEN 501
                        WHEN 'modbot.waf.blocked' THEN 502
                        WHEN 'modbot.migration.applied' THEN 503
                        WHEN 'modbot.retention.pruned' THEN 504
                        WHEN 'modbot.partition.created' THEN 505
                        WHEN 'modbot.user.purged' THEN 506
                        ELSE COALESCE(NULLIF(type_raw, '')::smallint, 0)
                    END);
                """);

            migrationBuilder.DropColumn(name: "type_raw", table: "modbot_event");
        }
    }
}
