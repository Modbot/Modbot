using System.Globalization;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Discord.Gateway;
using Modbot.Discord.Instances;

namespace Modbot.Discord.Giveaways;

/// <summary>How a giveaway's post reads right now.</summary>
public enum GiveawayCardState
{
    Open = 1,
    Closed = 2,
    Drawn = 3,
    Cancelled = 4,
}

/// <summary>
/// What a giveaway looks like in Discord: the channel post, in the style of the calendar's and the
/// instance cards (giveaways design §7.1).
/// </summary>
/// <remarks>
/// <para>
/// Times are Discord timestamps (<c>&lt;t:…:F&gt;</c>), so every reader sees them in their own
/// time zone and nobody has to convert.
/// </para>
/// <para>
/// <strong>The rules are on the card, in plain words.</strong> A giveaway whose rules live on a
/// page members cannot open is a giveaway they have to take on trust, and the whole point of the
/// fairness mechanics is that they should not have to.
/// </para>
/// <para>
/// A winner's name is VRChat or Discord text they chose themselves, so it is escaped like a
/// display name on an instance card. Mentions are off on every message the bot sends, so no card
/// can ping anybody however it is written (giveaways design §7.3).
/// </para>
/// </remarks>
public static class GiveawayCard
{
    /// <summary>Open: Modbot's own violet, not Discord's.</summary>
    private const uint Violet = 0x5B4BD6;
    private const uint Green = 0x3BA55D;
    private const uint Dark = 0x4F545C;
    private const uint Red = 0xED4245;

    /// <summary>How many winners are named on the card. Past this it says how many more there are.</summary>
    public const int MaxNamesShown = 20;

    /// <summary>How many rule lines fit in a field before the rest are summed up.</summary>
    public const int MaxRuleLines = 12;

    /// <param name="now">
    /// So the card can say "Opens" rather than "React with" for a giveaway that has been posted and
    /// whose entries have not started yet. Null reads as already open.
    /// </param>
    public static DiscordEmbedContent For(
        Giveaway giveaway,
        GiveawayCardState state,
        int entryCount,
        IReadOnlyList<GiveawayEntrant> winners,
        IReadOnlyDictionary<string, string>? roleNames = null,
        string? link = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(giveaway);
        ArgumentNullException.ThrowIfNull(winners);

        var rules = GiveawayRules.ReadStored(giveaway.Rules);
        var exclusions = GiveawayExclusions.ReadStored(giveaway.Exclusions);

        var fields = new List<DiscordEmbedField>();

        if (!string.IsNullOrWhiteSpace(giveaway.Prize))
            fields.Add(new DiscordEmbedField("Prize", Cut(giveaway.Prize, 1024), Inline: false));

        fields.Add(new DiscordEmbedField(
            state == GiveawayCardState.Open ? "Closes" : "Closed",
            $"{Stamp(giveaway.ClosesAt, "F")} ({Stamp(giveaway.ClosesAt, "R")})",
            Inline: false));

        fields.Add(new DiscordEmbedField("How to enter", HowToEnter(giveaway, state, now), Inline: true));
        fields.Add(new DiscordEmbedField(
            "Winners", giveaway.WinnerCount.ToString(CultureInfo.InvariantCulture), Inline: true));

        if (giveaway.Weighting != GiveawayWeights.Uniform)
        {
            var cap = giveaway.WeightCap is { } limit
                ? $", capped at {limit.ToString("N0", CultureInfo.InvariantCulture)}"
                : string.Empty;

            fields.Add(new DiscordEmbedField("Weighted by", GiveawayWeights.Label(giveaway.Weighting) + cap, Inline: true));
        }

        fields.Add(new DiscordEmbedField("Rules", RuleText(rules, roleNames), Inline: false));

        if (exclusions.Describe() is { Count: > 0 } kept)
            fields.Add(new DiscordEmbedField("Not eligible", Cut(string.Join(", ", kept), 1024), Inline: false));

        if (giveaway.EntryWay == GiveawayEntryWays.React && state != GiveawayCardState.Cancelled)
        {
            fields.Add(new DiscordEmbedField(
                "Entries", entryCount.ToString("N0", CultureInfo.InvariantCulture), Inline: true));
        }

        if (winners.Count > 0)
            fields.Add(new DiscordEmbedField(Word(winners.Count), Names(winners), Inline: false));

        return new DiscordEmbedContent(
            Title: Cut(giveaway.Name, 256),
            Description: null,
            Color: state switch
            {
                GiveawayCardState.Drawn => Green,
                GiveawayCardState.Closed => Dark,
                GiveawayCardState.Cancelled => Red,
                _ => Violet,
            },
            Fields: fields,
            Timestamp: giveaway.ClosesAt,
            Url: link,
            Footer: state switch
            {
                GiveawayCardState.Drawn => "Drawn",
                GiveawayCardState.Closed => "Closed",
                GiveawayCardState.Cancelled => "Cancelled",
                _ => "Open",
            });
    }

    /// <summary>
    /// What the bot says in the channel when a giveaway is drawn.
    /// </summary>
    /// <remarks>
    /// One line, naming the winners and linking the giveaway. Nobody is pinged: a giveaway with
    /// four hundred entrants would otherwise be four hundred notifications for one result, and the
    /// people who care are watching the post (giveaways design §7.3).
    /// </remarks>
    public static string Announcement(Giveaway giveaway, GiveawayDraw draw, IReadOnlyList<GiveawayEntrant> winners)
    {
        ArgumentNullException.ThrowIfNull(giveaway);
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(winners);

        var name = InstanceCard.Escape(giveaway.Name);

        if (winners.Count == 0)
            return $"**{name}** was drawn and nobody was in it.";

        var again = draw.Number > 1 ? $" (draw {draw.Number.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        var who = Names(winners);

        return winners.Count == 1
            ? $"**{name}**{again}: the winner is {who}."
            : $"**{name}**{again}: the winners are {who}.";
    }

    /// <summary>The buttons under the post: the giveaway on Modbot, when there is an address for it.</summary>
    public static IReadOnlyList<DiscordLinkButton> Links(string? link) =>
        link is { Length: > 0 } ? [new DiscordLinkButton("Details", link)] : [];

    private static string HowToEnter(Giveaway giveaway, GiveawayCardState state, DateTimeOffset? now) => state switch
    {
        GiveawayCardState.Cancelled => "Cancelled",
        GiveawayCardState.Drawn => "Drawn",
        GiveawayCardState.Closed => "Closed",
        _ when now is { } at && at < giveaway.OpensAt => $"Opens {Stamp(giveaway.OpensAt, "R")}",
        _ => giveaway.EntryWay == GiveawayEntryWays.React
            ? $"React with {giveaway.Emoji}"
            : "Nothing — everyone who matches is in",
    };

    private static string RuleText(GiveawayRule rule, IReadOnlyDictionary<string, string>? roleNames)
    {
        var lines = GiveawayRules.DescribeLines(rule, roleNames);

        if (lines.Count <= MaxRuleLines)
            return Cut(string.Join("\n", lines.Select(l => "• " + l)), 1024);

        var shown = lines.Take(MaxRuleLines).Select(l => "• " + l).ToList();
        shown.Add($"• and {(lines.Count - MaxRuleLines).ToString(CultureInfo.InvariantCulture)} more");

        return Cut(string.Join("\n", shown), 1024);
    }

    private static string Word(int winners) => winners == 1 ? "Winner" : "Winners";

    private static string Names(IReadOnlyList<GiveawayEntrant> winners)
    {
        var named = winners
            .OrderBy(w => w.WinnerRank)
            .Take(MaxNamesShown)
            .Select(w => InstanceCard.Escape(w.Purged ? "(erased)" : w.Name ?? w.Key))
            .ToList();

        var text = string.Join(", ", named);

        if (winners.Count > MaxNamesShown)
        {
            var more = (winners.Count - MaxNamesShown).ToString(CultureInfo.InvariantCulture);
            text += $", and {more} more";
        }

        return Cut(text, 1024);
    }

    private static string Stamp(DateTimeOffset at, string style) =>
        $"<t:{at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}:{style}>";

    private static string Cut(string value, int limit) =>
        value.Length <= limit ? value : value[..(limit - 1)] + "…";
}
