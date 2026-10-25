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

    /// <summary>No rule that gives the role is for this person any more.</summary>
    public const string NoLongerOnTheList = "No longer on the rule's list";

    /// <summary>The account was turned off or deleted, so no rule is for it any more.</summary>
    public const string AccountTurnedOff = "Account turned off";
}

/// <summary>Whether a moderator meets a rule: holds, or the first condition that does not.</summary>
/// <param name="Holds">Every condition the rule switches on is met.</param>
/// <param name="Reason">When it does not hold, which condition failed, in plain words.</param>
public readonly record struct RoleCheck(bool Holds, string? Reason)
{
    public static RoleCheck Met { get; } = new(true, null);

    public static RoleCheck Failed(string reason) => new(false, reason);
}

/// <summary>One condition of a rule and how a moderator stands against it.</summary>
/// <param name="Label">The condition's name in plain words.</param>
/// <param name="Met">The moderator meets it.</param>
/// <param name="Reason">When it is not met, the plain words the audit log uses. Null when it is met.</param>
/// <param name="Count">For a condition that counts: how many there are in the look-back. Null when there is nothing to count, or a number on the rule is missing.</param>
/// <param name="Max">For a condition that counts: the most the rule allows. Null when the rule has no number.</param>
/// <param name="Days">For a condition that counts over days: the look-back. Null for the daily ban cap, which always looks back 24 hours, and when the rule has no number.</param>
public sealed record RuleCondition(string Label, bool Met, string? Reason, int? Count, int? Max, int? Days);

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
        // The answer is the first failing line of the explanation, so the two can never disagree.
        var failed = Explain(rule, facts).FirstOrDefault(c => !c.Met);
        return failed is null ? RoleCheck.Met : RoleCheck.Failed(failed.Reason!);
    }

    /// <summary>
    /// Every condition the rule switches on, in the order <see cref="Check"/> goes through them, each
    /// with whether it is met and the numbers behind it. Conditions that are switched off are not listed.
    /// </summary>
    public static IReadOnlyList<RuleCondition> Explain(AutomaticRole rule, ModeratorFacts facts)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(facts);

        var conditions = new List<RuleCondition>();

        if (rule.CompanionRunning)
            conditions.Add(Simple("Companion running", facts.CompanionRunning, AutomaticRoleReasons.CompanionNotRunning));

        if (rule.ClipsReady)
            conditions.Add(Simple("Clips ready", facts.ClipsReady, AutomaticRoleReasons.ClipsTurnedOff));

        if (rule.InGroupInstance)
            conditions.Add(Simple("In a group instance", facts.InGroupInstance, AutomaticRoleReasons.NotInGroupInstance));

        if (rule.MinimumLevel)
            conditions.Add(Simple("Minimum level", ReachesMinimumLevel(rule, facts), AutomaticRoleReasons.MinimumLevelNotMet));

        if (rule.OverturnedBans)
        {
            conditions.Add(Counted(
                "Overturned bans", facts.OverturnedDecidedAt, rule.OverturnedBansMax, rule.OverturnedBansDays,
                rule.OverturnedBansDays is { } days ? TimeSpan.FromDays(days) : null, facts.Now,
                AutomaticRoleReasons.TooManyOverturnedBans));
        }

        if (rule.DailyBanCap)
        {
            conditions.Add(Counted(
                "Daily ban cap", facts.BanTimes, rule.DailyBanCapMax, null, DailyWindow, facts.Now,
                AutomaticRoleReasons.DailyBanCapReached));
        }

        if (rule.MissedClips)
        {
            conditions.Add(Counted(
                "Missed clips", facts.MissedClipTimes, rule.MissedClipsMax, rule.MissedClipsDays,
                rule.MissedClipsDays is { } days ? TimeSpan.FromDays(days) : null, facts.Now,
                AutomaticRoleReasons.TooManyMissedClips));
        }

        return conditions;
    }

    private static RuleCondition Simple(string label, bool met, string failedReason)
        => new(label, met, met ? null : failedReason, null, null, null);

    // "At most N" allows N. A missing number or look-back never holds, and has no count to show.
    private static RuleCondition Counted(
        string label, IReadOnlyList<DateTimeOffset> times, int? max, int? days, TimeSpan? window,
        DateTimeOffset now, string tooManyReason)
    {
        if (max is not { } limit || window is not { } lookBack)
            return new RuleCondition(label, false, AutomaticRoleReasons.NumberMissing, null, max, days);

        var count = CountSince(times, now - lookBack);
        return count > limit
            ? new RuleCondition(label, false, tooManyReason, count, limit, days)
            : new RuleCondition(label, true, null, count, limit, days);
    }

    // The staff trial and the Discord staff role are each enough on their own; a rule that names
    // neither can never be met.
    private static bool ReachesMinimumLevel(AutomaticRole rule, ModeratorFacts facts)
        => (rule.MinimumLevelPassedTrial && facts.PassedTrial)
            || (rule.MinimumLevelStaffRoleId is { Length: > 0 } role && facts.DiscordRoleIds.Contains(role));

    private static int CountSince(IReadOnlyList<DateTimeOffset> times, DateTimeOffset since)
        => times.Count(t => t >= since);
}
