using System.Globalization;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;

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
/// <strong>A winner is a linked name</strong>, through <see cref="CardLink"/>, like every other
/// person on every other card (Discord embeds design §2). A card announcing that somebody won is
/// the last place a moderator should have nothing to click. Without a public address the same
/// helper gives the name on its own, and an erased entrant stays the plain words that replaced
/// them.
/// </para>
/// <para>
/// A winner's name and a Discord role's name are text somebody else chose, so both are made inert
/// before they reach a card. Mentions are off on every message the bot sends as well, so no card
/// can ping anybody however it is written (giveaways design §7.3).
/// </para>
/// </remarks>
public static class GiveawayCard
{
    /// <summary>How many winners are named on the card. Past this it says how many more there are.</summary>
    public const int MaxNamesShown = 20;

    /// <summary>How many rule lines fit in a field before the rest are summed up.</summary>
    public const int MaxRuleLines = 12;

    /// <summary>Discord's limit on one embed field's value, in characters.</summary>
    public const int FieldValueLimit = 1024;

    /// <summary>What a winner who asked to be erased is called (giveaways design §6.3).</summary>
    private const string Erased = "(erased)";

    /// <param name="now">
    /// So the card can say "Opens" rather than "React with" for a giveaway that has been posted and
    /// whose entries have not started yet. Null reads as already open.
    /// </param>
    /// <param name="style">
    /// Where Modbot is, whose group is giving something away, and the mark beside the footer. The
    /// winners link into Modbot through it and the group's name sits above the title, the way it
    /// does on an instance and a calendar post.
    /// </param>
    /// <param name="picture">
    /// <see cref="CardPicture.AuthorIcon"/> is the group's icon, already sent with the message
    /// (Discord embeds design §3.6). There is no thumbnail and no large picture: Modbot holds no
    /// picture of a prize, and this card is about neither a person nor a world.
    /// </param>
    public static DiscordEmbedContent For(
        Giveaway giveaway,
        GiveawayCardState state,
        int entryCount,
        IReadOnlyList<GiveawayEntrant> winners,
        IReadOnlyDictionary<string, string>? roleNames = null,
        string? link = null,
        DateTimeOffset? now = null,
        CardStyle? style = null,
        CardPicture picture = default)
    {
        ArgumentNullException.ThrowIfNull(giveaway);
        ArgumentNullException.ThrowIfNull(winners);

        style ??= CardStyle.None;

        var rules = GiveawayRules.ReadStored(giveaway.Rules);
        var exclusions = GiveawayExclusions.ReadStored(giveaway.Exclusions);

        var fields = new List<DiscordEmbedField>();

        // The prize is the organiser's own words and keeps their formatting, the way a calendar
        // event's description does.
        if (!string.IsNullOrWhiteSpace(giveaway.Prize))
            fields.Add(new DiscordEmbedField("Prize", CardText.Fit(giveaway.Prize, FieldValueLimit), Inline: false));

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
        {
            fields.Add(new DiscordEmbedField(
                "Not eligible", CardText.Fit(string.Join(", ", kept), FieldValueLimit), Inline: false));
        }

        if (giveaway.EntryWay == GiveawayEntryWays.React && state != GiveawayCardState.Cancelled)
        {
            fields.Add(new DiscordEmbedField(
                "Entries", entryCount.ToString("N0", CultureInfo.InvariantCulture), Inline: true));
        }

        if (winners.Count > 0)
            fields.Add(new DiscordEmbedField(Word(winners.Count), Names(winners, style), Inline: false));

        return new DiscordEmbedContent(
            Title: CardText.Plain(giveaway.Name, 256),
            Description: null,
            Color: state switch
            {
                GiveawayCardState.Drawn => CardColour.Green,
                GiveawayCardState.Closed => CardColour.Dark,
                GiveawayCardState.Cancelled => CardColour.Red,
                _ => CardColour.Violet,
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
            },
            FooterIconUrl: style.FooterIconUrl,
            AuthorName: style.GroupName is { Length: > 0 } group ? CardText.Plain(group, 256) : null,
            AuthorIconUrl: picture.AuthorIcon);
    }

    /// <summary>
    /// What the bot says in the channel when a giveaway is drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One line, naming the winners and linking the giveaway. Nobody is pinged: a giveaway with
    /// four hundred entrants would otherwise be four hundred notifications for one result, and the
    /// people who care are watching the post (giveaways design §7.3).
    /// </para>
    /// <para>
    /// <strong>The names here are plain, unlike the ones on the card.</strong> This is a line of
    /// message text rather than an embed, and the linked names belong where Discord is certain to
    /// draw them as names -- the card directly above, which the announcement points at.
    /// </para>
    /// </remarks>
    public static string Announcement(Giveaway giveaway, GiveawayDraw draw, IReadOnlyList<GiveawayEntrant> winners)
    {
        ArgumentNullException.ThrowIfNull(giveaway);
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(winners);

        var name = CardText.EscapeName(giveaway.Name);

        if (winners.Count == 0)
            return $"**{name}** was drawn and nobody was in it.";

        var again = draw.Number > 1 ? $" (draw {draw.Number.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
        var who = Names(winners, style: null);

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

    /// <summary>
    /// The rules as a bullet list.
    /// </summary>
    /// <remarks>
    /// Every line is made inert, because a rule about a role prints that role's name and a Discord
    /// role is named by whoever made it. The rest of a line is Modbot's own words, which say the
    /// same thing escaped or not.
    /// </remarks>
    private static string RuleText(GiveawayRule rule, IReadOnlyDictionary<string, string>? roleNames)
    {
        var lines = GiveawayRules.DescribeLines(rule, roleNames)
            .Select(l => "• " + CardText.EscapeName(l))
            .ToList();

        if (lines.Count > MaxRuleLines)
        {
            var more = (lines.Count - MaxRuleLines).ToString(CultureInfo.InvariantCulture);
            lines = [.. lines.Take(MaxRuleLines), $"• and {more} more"];
        }

        return CardText.Fit(string.Join("\n", lines), FieldValueLimit);
    }

    private static string Word(int winners) => winners == 1 ? "Winner" : "Winners";

    /// <summary>
    /// The winners as one field value, best first, and "and N more" for the rest.
    /// </summary>
    /// <remarks>
    /// The list is built against Discord's field limit rather than cut to it. A linked name is
    /// four times the length of a plain one, so twenty of them run past 1024 characters where
    /// twenty plain ones would not -- and a cut that landed inside <c>[…](…)</c> would put a raw
    /// address on the card.
    /// </remarks>
    /// <param name="style">
    /// Where Modbot is, so each name links to the person. Null for the announcement, which is
    /// message text rather than a card.
    /// </param>
    private static string Names(IReadOnlyList<GiveawayEntrant> winners, CardStyle? style)
    {
        var ordered = winners.OrderBy(w => w.WinnerRank).ToList();
        var listed = new List<string>();
        var length = 0;

        foreach (var winner in ordered.Take(MaxNamesShown))
        {
            var name = Name(winner, style);

            // Room is kept for the longest "and N more" this list could end with, so adding a name
            // can never push the finished value past the limit.
            var more = Suffix(ordered.Count - listed.Count - 1);
            var next = length + (listed.Count > 0 ? 2 : 0) + name.Length;

            if (next + (more.Length > 0 ? 2 + more.Length : 0) > FieldValueLimit)
                break;

            listed.Add(name);
            length = next;
        }

        var remaining = ordered.Count - listed.Count;

        if (remaining > 0)
            listed.Add(Suffix(remaining));

        return string.Join(", ", listed);
    }

    private static string Suffix(int remaining) =>
        remaining <= 0 ? string.Empty : $"and {remaining.ToString(CultureInfo.InvariantCulture)} more";

    /// <summary>One winner as the card names them.</summary>
    /// <remarks>
    /// Somebody erased at their own request keeps their place in the draw and loses their name and
    /// their ids, so there is nothing to link to and the words that replaced them are all the card
    /// can show (giveaways design §6.3).
    /// </remarks>
    private static string Name(GiveawayEntrant winner, CardStyle? style)
    {
        if (winner.Purged)
            return CardText.EscapeName(Erased);

        if (style is not null)
        {
            if (winner.VRChatUserId is { Length: > 0 } vrchat)
                return CardLink.Person(winner.Name, vrchat, style.PublicAddress);

            if (winner.DiscordUserId is { Length: > 0 } discord)
                return CardLink.DiscordPerson(winner.Name, discord, style.PublicAddress);
        }

        return CardText.Fit(CardText.EscapeName(winner.Name ?? winner.Key), CardText.MaxNameLength);
    }

    private static string Stamp(DateTimeOffset at, string style) =>
        $"<t:{at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}:{style}>";
}
