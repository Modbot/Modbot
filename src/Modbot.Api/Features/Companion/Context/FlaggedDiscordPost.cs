using Modbot.Analytics.Facts;
using Modbot.Core.Discord;

namespace Modbot.Api.Features.Companion.Context;

/// <summary>
/// Posts a flagged alert in the Discord channel the Flagged settings name, when they say to.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One helper for every flagged alert.</strong> A flagged join uses it today; the avatar
/// watch and the crash alert say their sentence through <see cref="PostAsync"/> the same way, so
/// the switch and the channel are checked in one place.
/// </para>
/// <para>
/// <strong>Every flagged join, not only watched people.</strong> The notification to the team
/// (<c>WatchAlerts</c>) is for a watch; this is for whatever the flag rules matched. The person is
/// named, because the channel is one the group picked for its staff, which is not true of an email
/// or a direct message.
/// </para>
/// </remarks>
public static class FlaggedDiscordPost
{
    /// <summary>Says <paramref name="text"/> in the channel, if the setting is on and a channel is picked.</summary>
    /// <returns>Whether Discord took it. False when the setting is off, no channel is picked or Discord refused.</returns>
    public static async Task<bool> PostAsync(
        IDiscordChannelPoster discord,
        FlagRuleSettings rules,
        string text,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(discord);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(text);

        if (!rules.DiscordPost || rules.DiscordChannelId is not { Length: > 0 } channel)
            return false;

        return await discord.PostAsync(channel, text, ct);
    }

    /// <summary>One post for each flagged person in <paramref name="joins"/>.</summary>
    /// <param name="joins">The genuine joins in this batch, first report only.</param>
    /// <param name="flagged">What the flag rules decided for the people in them.</param>
    /// <param name="nameOf">The display name the batch carried for a person, if it did.</param>
    /// <returns>How many posts Discord took.</returns>
    public static async Task<int> RaiseForJoinsAsync(
        IDiscordChannelPoster discord,
        FlagRuleSettings rules,
        IReadOnlyList<FactRecord> joins,
        IReadOnlyDictionary<string, FlagMatch> flagged,
        Func<string, string?> nameOf,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(discord);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(joins);
        ArgumentNullException.ThrowIfNull(flagged);
        ArgumentNullException.ThrowIfNull(nameOf);

        if (!rules.DiscordPost)
            return 0;

        var posted = 0;

        foreach (var join in joins)
        {
            if (flagged.GetValueOrDefault(join.SubjectId) is not { IsFlagged: true } match)
                continue;

            if (await PostAsync(discord, rules, JoinText(nameOf(join.SubjectId), match), ct))
                posted++;
        }

        return posted;
    }

    /// <summary>The sentence for a flagged join: who, and the reasons in the words the join card uses.</summary>
    public static string JoinText(string? displayName, FlagMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        var who = string.IsNullOrWhiteSpace(displayName) ? "Someone" : displayName.Trim();

        return $"Flagged: {who} joined a group instance. {match.Reason}";
    }
}
