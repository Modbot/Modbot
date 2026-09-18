using System.Text.Json;
using Modbot.Core.Data.Entities;

namespace Modbot.Moderation;

/// <summary>
/// Everything that stands between a rule matching and a rule acting (AI moderation design §13,
/// AutoMod design §5).
/// </summary>
/// <remarks>
/// A rule that only flags passes none of this: it is all about actions. The order is scope, then
/// exempt roles, then the trial, then a pause — from "this rule does not apply here" through to
/// "this rule applied and Modbot stopped it anyway".
/// </remarks>
public static class RuleGuards
{
    /// <summary>The rule asks for anything beyond a flag, on either platform.</summary>
    public static bool WantsAction(IModerationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return WantsDiscordAction(rule) || WantsVRChatAction(rule);
    }

    /// <summary>The rule asks to delete a Discord message or time its author out.</summary>
    public static bool WantsDiscordAction(IModerationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.DeleteMessage || rule.TimeoutMinutes is > 0;
    }

    /// <summary>The rule asks to ban a person from the VRChat group or remove them from it (AutoMod design §5).</summary>
    public static bool WantsVRChatAction(IModerationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.GroupBan || rule.GroupRemove;
    }

    /// <summary>The rule wants to act and its trial has started and not been ended.</summary>
    public static bool InTrial(IModerationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return WantsAction(rule) && rule.TrialStartedAt is not null && rule.TrialEndedAt is null;
    }

    /// <summary>The rule acts for real: it wants to, its trial is over, and it is not paused.</summary>
    public static bool ActsNow(IModerationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return WantsAction(rule) && !InTrial(rule) && rule.PausedAt is null;
    }

    /// <summary>When the trial is suggested to end. Null when there is no trial running.</summary>
    public static DateTimeOffset? TrialEndsAt(IModerationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.TrialStartedAt is { } started && rule.TrialEndedAt is null
            ? started.AddDays(Math.Clamp(rule.TrialDays, MinTrialDays, MaxTrialDays))
            : null;
    }

    public const int MinTrialDays = 1;

    public const int MaxTrialDays = 90;

    public const int DefaultTrialDays = 7;

    /// <summary>Whether the rule runs in this channel at all (design §13.3).</summary>
    /// <remarks>
    /// A channel limit stops the rule entirely, flags included: "this rule is not for that channel"
    /// is a different statement from "this rule does not act on that person".
    /// </remarks>
    public static bool RunsIn(IModerationRule rule, string? channelId)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (channelId is null || rule.ChannelMode == ChannelScope.All)
            return true;

        var listed = Ids(rule.Channels).Contains(channelId, StringComparer.Ordinal);
        return rule.ChannelMode == ChannelScope.Only ? listed : !listed;
    }

    /// <summary>The exempt role the person holds, or null when they hold none (design §13.3).</summary>
    public static string? ExemptBy(IModerationRule rule, IReadOnlyCollection<string>? roleIds)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (roleIds is null || roleIds.Count == 0)
            return null;

        foreach (var exempt in Ids(rule.ExemptRoles))
        {
            if (roleIds.Contains(exempt, StringComparer.Ordinal))
                return exempt;
        }

        return null;
    }

    /// <summary>A stored JSON array of ids, or an empty list when it is not one.</summary>
    public static IReadOnlyList<string> Ids(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// When a rule that acts has run away with itself (AI moderation design §13.2).
/// </summary>
/// <remarks>
/// <para>
/// A rule set to act on a Monday can be wrong about a whole server on the Tuesday: a pattern that
/// matches more than its author thought, a Hub list updated under it, a raid that makes every
/// message match. The check has to work from a standing start, when the rule has no history to be
/// measured against, so it is two conditions and both must hold: more than ten actions in an hour,
/// <em>and</em> more than sixteen times the rule's own hourly average over the last seven days.
/// </para>
/// <para>
/// A brand new rule has an average of zero, so the first condition is the one that decides: eleven
/// actions in an hour and it stops. A rule that normally acts twice an hour clears the first
/// condition long before the second, so the second is what decides for it: it has to reach 33 in
/// an hour before it stops. Both numbers are Modbot's, not the operator's: a number somebody can
/// raise is a number somebody raises.
/// </para>
/// </remarks>
public static class RunawayGuard
{
    /// <summary>More than this many actions in an hour is the first condition.</summary>
    public const int ActionsInAnHour = 10;

    /// <summary>More than this many times the last seven days' hourly average is the second.</summary>
    public const double TimesTheAverage = 16;

    private const double HoursInSevenDays = 7 * 24;

    /// <summary>Whether a rule with these counts should pause itself.</summary>
    public static bool ShouldPause(int actionsInTheLastHour, int actionsInTheLastSevenDays)
        => actionsInTheLastHour > ActionsInAnHour
           && actionsInTheLastHour > TimesTheAverage * (actionsInTheLastSevenDays / HoursInSevenDays);

    /// <summary>What the operator reads on the rule's card and in the fact.</summary>
    public static string Reason(int actionsInTheLastHour, int actionsInTheLastSevenDays)
    {
        var average = actionsInTheLastSevenDays / HoursInSevenDays;
        return $"Acted {actionsInTheLastHour} times in an hour, against {average:0.#} an hour over the last 7 days.";
    }
}
