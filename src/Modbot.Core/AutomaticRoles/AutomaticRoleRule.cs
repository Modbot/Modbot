using Modbot.Core.Data.Entities;

namespace Modbot.Core.AutomaticRoles;

/// <summary>The plain words for why a moderator does not hold an automatic role. They go to the audit log as they are.</summary>
public static class AutomaticRoleReasons
{
    public const string CompanionNotRunning = "Companion not running";
    public const string ClipsTurnedOff = "Clips turned off";
    public const string NotInGroupInstance = "Not in a group instance";
    public const string MinimumLevelNotMet = "Minimum level not met";
    public const string TooManyOverturnedBans = "Too many overturned bans";
    public const string DailyBanCapReached = "Daily ban cap reached";
    public const string TooManyMissedClips = "Too many missed clips";

    /// <summary>A number the rule needs is not filled in. The role is not given until it is.</summary>
    public const string NumberMissing = "A number on the rule is missing";
}

/// <summary>Whether a moderator meets a rule: holds, or the first condition that does not.</summary>
/// <param name="Holds">Every condition the rule switches on is met.</param>
/// <param name="Reason">When it does not hold, which condition failed, in plain words.</param>
public readonly record struct RoleCheck(bool Holds, string? Reason)
{
    public static RoleCheck Met { get; } = new(true, null);

    public static RoleCheck Failed(string reason) => new(false, reason);
}

/// <summary>
/// Whether a moderator should hold an automatic role right now. Pure: no database, no clock, no
/// VRChat or Discord -- the caller gathers the <see cref="ModeratorFacts"/> and acts on the answer.
/// </summary>
/// <remarks>
/// <para>
/// The conditions are checked in the order the rule lists them (companion, clips, instance, level,
/// overturned bans, daily cap, missed clips) and the first one that fails is the answer, so the
/// reason in the audit log is always the same one for the same facts.
/// </para>
/// <para>
/// "At most N" means N is allowed: one over takes the role. N = 0 means the first one takes it.
/// A switched-on condition whose number is missing never holds, rather than guessing a number.
/// </para>
/// </remarks>
public static class AutomaticRoleRule
{
    /// <summary>The look-back of the daily ban cap.</summary>
    public static readonly TimeSpan DailyWindow = TimeSpan.FromHours(24);

    /// <summary>Whether the rule is for this account: the whole moderator team, or members of the roles it names.</summary>
    public static bool AppliesTo(AutomaticRole rule, ModeratorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(facts);

        if (rule.WholeTeam)
            return facts.IsModerator;

        return rule.WhoDiscordRoleIds.Any(id => facts.DiscordRoleIds.Contains(id))
            || rule.WhoVRChatRoleIds.Any(id => facts.VRChatRoleIds.Contains(id));
    }

    /// <summary>Whether every condition the rule switches on holds for these facts, or the first that does not.</summary>
    public static RoleCheck Check(AutomaticRole rule, ModeratorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(facts);

        if (rule.CompanionRunning && !facts.CompanionRunning)
            return RoleCheck.Failed(AutomaticRoleReasons.CompanionNotRunning);

        if (rule.ClipsReady && !facts.ClipsReady)
            return RoleCheck.Failed(AutomaticRoleReasons.ClipsTurnedOff);

        if (rule.InGroupInstance && !facts.InGroupInstance)
            return RoleCheck.Failed(AutomaticRoleReasons.NotInGroupInstance);

        if (rule.MinimumLevel && !ReachesMinimumLevel(rule, facts))
            return RoleCheck.Failed(AutomaticRoleReasons.MinimumLevelNotMet);

        if (rule.OverturnedBans)
        {
            if (rule.OverturnedBansMax is not { } max || rule.OverturnedBansDays is not { } days)
                return RoleCheck.Failed(AutomaticRoleReasons.NumberMissing);

            if (CountSince(facts.OverturnedDecidedAt, facts.Now - TimeSpan.FromDays(days)) > max)
                return RoleCheck.Failed(AutomaticRoleReasons.TooManyOverturnedBans);
        }

        if (rule.DailyBanCap)
        {
            if (rule.DailyBanCapMax is not { } max)
                return RoleCheck.Failed(AutomaticRoleReasons.NumberMissing);

            if (CountSince(facts.BanTimes, facts.Now - DailyWindow) > max)
                return RoleCheck.Failed(AutomaticRoleReasons.DailyBanCapReached);
        }

        if (rule.MissedClips)
        {
            if (rule.MissedClipsMax is not { } max || rule.MissedClipsDays is not { } days)
                return RoleCheck.Failed(AutomaticRoleReasons.NumberMissing);

            if (CountSince(facts.MissedClipTimes, facts.Now - TimeSpan.FromDays(days)) > max)
                return RoleCheck.Failed(AutomaticRoleReasons.TooManyMissedClips);
        }

        return RoleCheck.Met;
    }

    // The staff trial and the Discord staff role are each enough on their own; a rule that names
    // neither can never be met.
    private static bool ReachesMinimumLevel(AutomaticRole rule, ModeratorFacts facts)
        => (rule.MinimumLevelPassedTrial && facts.PassedTrial)
            || (rule.MinimumLevelStaffRoleId is { Length: > 0 } role && facts.DiscordRoleIds.Contains(role));

    private static int CountSince(IReadOnlyList<DateTimeOffset> times, DateTimeOffset since)
        => times.Count(t => t >= since);
}
