namespace Modbot.Core.Data.Entities;

/// <summary>
/// One automatic role rule: a VRChat group role, a Discord role, or one of each, held by a
/// moderator only while every condition the rule switches on holds. The table is
/// <c>automatic_role</c>.
/// </summary>
/// <remarks>
/// <para>
/// This row only stores the rule. Checking the conditions and giving or taking the roles are done
/// elsewhere; the row never says whether anyone holds the role right now.
/// </para>
/// <para>
/// Role ids are text and never parsed or checked for a shape. Any role the leads pick is allowed:
/// there is no permission check on the role itself.
/// </para>
/// </remarks>
public class AutomaticRole
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>What the leads call the rule. Optional.</summary>
    public string? Name { get; set; }

    /// <summary>The VRChat group role to give. At least one of this and <see cref="DiscordRoleId"/> is set.</summary>
    public string? VRChatRoleId { get; set; }

    /// <summary>The Discord role to give.</summary>
    public string? DiscordRoleId { get; set; }

    /// <summary>The rule applies to the whole Modbot moderator team. When false, only to members of the roles below.</summary>
    public bool WholeTeam { get; set; } = true;

    /// <summary>The Discord roles the rule applies to, when it is not for the whole team.</summary>
    public List<string> WhoDiscordRoleIds { get; set; } = [];

    /// <summary>The VRChat group roles the rule applies to, when it is not for the whole team.</summary>
    public List<string> WhoVRChatRoleIds { get; set; } = [];

    /// <summary>The companion is running (its live connection is up).</summary>
    public bool CompanionRunning { get; set; }

    /// <summary>
    /// Clips on, the 10-minute setting and "Send clips for my actions automatically" ticked. Only
    /// allowed while the group has evidence storage set up.
    /// </summary>
    public bool ClipsReady { get; set; }

    /// <summary>In a group instance right now.</summary>
    public bool InGroupInstance { get; set; }

    /// <summary>Has reached a minimum level: <see cref="MinimumLevelPassedTrial"/> and/or <see cref="MinimumLevelStaffRoleId"/>.</summary>
    public bool MinimumLevel { get; set; }

    /// <summary>Passed the staff trial.</summary>
    public bool MinimumLevelPassedTrial { get; set; }

    /// <summary>Holds this Discord staff role.</summary>
    public string? MinimumLevelStaffRoleId { get; set; }

    /// <summary>At most <see cref="OverturnedBansMax"/> of their bans overturned on appeal in the last <see cref="OverturnedBansDays"/> days.</summary>
    public bool OverturnedBans { get; set; }

    public int? OverturnedBansMax { get; set; }

    public int? OverturnedBansDays { get; set; }

    /// <summary>At most <see cref="DailyBanCapMax"/> bans in the last 24 hours.</summary>
    public bool DailyBanCap { get; set; }

    public int? DailyBanCapMax { get; set; }

    /// <summary>At most <see cref="MissedClipsMax"/> missed clips in the last <see cref="MissedClipsDays"/> days. 0 is allowed.</summary>
    public bool MissedClips { get; set; }

    public int? MissedClipsMax { get; set; }

    public int? MissedClipsDays { get; set; }

    /// <summary>
    /// The Modbot users whose hand-given role Modbot may take back. For anyone else, a role they
    /// already held that Modbot did not give is left alone.
    /// </summary>
    public List<Guid> TakeBackUserIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
