namespace Modbot.Core.Data.Entities;

/// <summary>Which side of a role pair wins when the two disagree (M5 §3.1).</summary>
/// <remarks>
/// Stored as text so the database, the API and the web app all read the same word.
/// <see cref="Nobody"/> is the safe starting point: Modbot records the disagreement and changes
/// nothing, which is what a group that has not decided yet should get.
/// </remarks>
public static class RoleSyncDecides
{
    public const string VRChat = "vrchat";
    public const string Discord = "discord";
    public const string Nobody = "nobody";

    public static bool IsKnown(string? value) => value is VRChat or Discord or Nobody;
}

/// <summary>What a copied VRChat ban does in Discord.</summary>
public static class DiscordBanCopyActions
{
    public const string Ban = "ban";
    public const string Remove = "remove";

    public static bool IsKnown(string? value) => value is Ban or Remove;
}

/// <summary>
/// One VRChat group role paired with one Discord role, and which side decides. The table is
/// <c>discord_role_pair</c>.
/// </summary>
/// <remarks>
/// <para>
/// M5 §3.1: bidirectional sync with no side designated as right produces flapping, so the decision
/// belongs to the pair rather than to the deployment. One group can have "Staff" decided in VRChat
/// and "Event host" decided in Discord at the same time, which is what real groups actually want.
/// </para>
/// <para>
/// <strong>Only paired roles are touched, ever</strong> (§3.2). Every other role on both sides is
/// left exactly as it is, and there is no "manage everything except" list — a role nobody has
/// paired here is not Modbot's business.
/// </para>
/// </remarks>
public class DiscordRolePair
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The VRChat group role's id. Opaque text, never validated (foundation §3.1.1).</summary>
    public string VRChatRoleId { get; set; } = string.Empty;

    /// <summary>The Discord role's id. Text, never parsed.</summary>
    public string DiscordRoleId { get; set; } = string.Empty;

    /// <summary>
    /// What each role was called when the pair was last saved.
    /// </summary>
    /// <remarks>
    /// Kept on the row so that a fact about a role change can say "Staff" instead of
    /// <c>grol_9f3c…</c> years later, the same reason the group's own role list is recorded. The
    /// live names are shown from each platform's role list; these are the ones written into
    /// history.
    /// </remarks>
    public string? VRChatRoleName { get; set; }

    /// <inheritdoc cref="VRChatRoleName"/>
    public string? DiscordRoleName { get; set; }

    /// <summary>One of <see cref="RoleSyncDecides"/>.</summary>
    public string Decides { get; set; } = RoleSyncDecides.Nobody;

    /// <summary>Off leaves the pair recorded and stops Modbot looking at it.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The last change this pair asked for that was refused, as a sentence.</summary>
    public string? Problem { get; set; }
}

/// <summary>What Modbot copied from one platform to the other, and which way.</summary>
public static class CopyDirections
{
    /// <summary>Something that happened in the VRChat group, copied into Discord.</summary>
    public const string ToDiscord = "to-discord";

    /// <summary>Something that happened in Discord, copied into the VRChat group.</summary>
    public const string ToVRChat = "to-vrchat";
}

/// <summary>What kind of change a copy was.</summary>
public static class CopyKinds
{
    public const string Ban = "ban";
    public const string Unban = "unban";
    public const string Remove = "remove";
    public const string RoleGiven = "role-given";
    public const string RoleTaken = "role-taken";
}

/// <summary>
/// One change Modbot made on one platform because of something that happened on the other. The
/// table is <c>discord_copied_action</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is how the loop is broken</strong> (M5 §4.2). A ban Modbot copies into Discord
/// comes back moments later as a Discord ban event, and copying that back into VRChat would start
/// a circle that never closes and writes duplicate facts forever. So a row is written
/// <em>before</em> the change is sent, and every incoming ban, unban or role change is checked
/// against the rows first: an event Modbot caused is recognised and dropped.
/// </para>
/// <para>
/// <strong>One row answers for one returning event, and then stops answering.</strong> Once the
/// event a copy caused has come back and been dropped, <see cref="SeenBackAt"/> is set and the row
/// is finished. If a person bans the same member again an hour later, that ban is a new event with
/// no row to excuse it, and it is copied like any other. Without that, one copy would silently
/// swallow every later ban of the same person.
/// </para>
/// <para>
/// <strong>Why not a mark on the fact.</strong> Facts are immutable and are written by whatever
/// observed the event — the Discord audit-log reader, the VRChat member sweep — long after the
/// copy was sent, and often by code that has no idea a sync exists. A row written before the
/// outbound call is the only record that is certain to exist by the time the event arrives.
/// </para>
/// </remarks>
public class CopiedAction
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>One of <see cref="CopyDirections"/>: which platform was written to.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>One of <see cref="CopyKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The person on the platform written to. Opaque text.</summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>The same person on the platform the change came from, when it is known.</summary>
    public string? OtherSideId { get; set; }

    /// <summary>The role given or taken away, on the platform written to. Null for a ban.</summary>
    public string? RoleId { get; set; }

    /// <summary>The fact that caused this copy, when one did.</summary>
    public long? CausedByFactId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Null while the call is out; true once the platform accepted it.</summary>
    public bool? Done { get; set; }

    /// <summary>What the platform said when it refused.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// When the event this copy caused came back and was dropped. Set once, and a row that has it
    /// excuses nothing further.
    /// </summary>
    public DateTimeOffset? SeenBackAt { get; set; }
}

/// <summary>
/// Where the sync has got to. One row, id 1. The table is <c>discord_sync_state</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ban sync reads the fact log rather than listening to events, so that what it acts on is the
/// same record a moderator reads and a restart loses nothing: a ban seen while Modbot was down is
/// still a fact once the audit log is caught up, and it is still copied. This row is how far it
/// has read.
/// </para>
/// <para>
/// <strong>Switching ban sync on starts from now, not from the beginning.</strong> The marker is
/// moved to the newest fact the moment a direction is switched on, so turning it on does not
/// replay years of history as a flood of bans. The backlog is the operator's decision, made from
/// the catch-up screen where they can see it first (M5 §7).
/// </para>
/// </remarks>
public class DiscordSyncState
{
    public int Id { get; set; } = 1;

    /// <summary>The newest fact ban sync has considered. Nothing at or below it is looked at again.</summary>
    public long BansReadThrough { get; set; }

    public DateTimeOffset? BansReadAt { get; set; }

    /// <summary>The last thing ban sync could not do, as a sentence.</summary>
    public string? BansProblem { get; set; }

    public DateTimeOffset? RolesRanAt { get; set; }

    /// <summary>The last thing role sync could not do, as a sentence.</summary>
    public string? RolesProblem { get; set; }
}
