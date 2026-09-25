using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <summary>
    /// Fills in the names of roles and voice channels on facts that only kept their ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two facts were written with an id and no name, so the log could only say "a Discord role"
    /// and "a Discord voice channel" — which for a page whose whole job is saying what happened is
    /// most of the sentence missing. Both now carry the name when they are written, and this is the
    /// same answer for the rows already there.
    /// </para>
    /// <para>
    /// Only rows that have the id and not the name, and only where the server index still knows
    /// what the thing is called. A role or channel deleted before Modbot indexed it is left as it
    /// is; the sentence says the general phrase and that is the truth.
    /// </para>
    /// <para>
    /// The name is the one the index holds <em>now</em>, not the one it had on the day. A role
    /// that has been renamed since will read by its current name, which is the same thing every
    /// other name on the page does — the audit log shows a person by the name they go by today,
    /// not the one they used when they were banned.
    /// </para>
    /// <para>
    /// There is no Down. Removing a name would not restore anything: the rows this fills were
    /// missing a value, not holding a different one, and a migration that deletes data to undo
    /// writing data is worse than one that does nothing.
    /// </para>
    /// </remarks>
    public partial class NameTheRoleAndTheVoiceChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE modbot_event e
                SET data = jsonb_set(e.data, '{roleName}', to_jsonb(r.name))
                FROM discord_role r
                WHERE e.type IN ('discord.role.assign', 'discord.role.unassign')
                  AND e.data ? 'roleId'
                  AND NOT (e.data ? 'roleName')
                  AND r.role_id = e.data ->> 'roleId'
                  AND r.name <> '';
                """);

            migrationBuilder.Sql("""
                UPDATE modbot_event e
                SET data = jsonb_set(e.data, '{channelName}', to_jsonb(c.name))
                FROM discord_channel c
                WHERE e.type IN ('discord.voice.join', 'discord.voice.leave', 'discord.voice.move')
                  AND e.data ? 'channelId'
                  AND NOT (e.data ? 'channelName')
                  AND c.channel_id = e.data ->> 'channelId'
                  AND c.name <> '';
                """);

            // A move also remembers where they came from.
            migrationBuilder.Sql("""
                UPDATE modbot_event e
                SET data = jsonb_set(e.data, '{fromName}', to_jsonb(c.name))
                FROM discord_channel c
                WHERE e.type = 'discord.voice.move'
                  AND e.data ? 'from'
                  AND NOT (e.data ? 'fromName')
                  AND c.channel_id = e.data ->> 'from'
                  AND c.name <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
