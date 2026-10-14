using System.Globalization;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Votes;

/// <summary>
/// What a vote looks like in Discord, and the ids its buttons and drop-down lists carry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ids.</strong> A button is <c>modbot:vote:&lt;vote&gt;:&lt;option&gt;</c>; a drop-down list is
/// <c>modbot:vote:&lt;vote&gt;:list&lt;n&gt;</c> with the options' ids as its values. The prefix is distinct
/// from every other feature's. A press only ever names a vote and an option by id: whether the vote
/// is still open, who may vote and what the options are is looked up again when it is pressed.
/// </para>
/// <para>
/// <strong>Buttons up to 25, lists beyond.</strong> Discord puts at most 25 buttons on a message, in
/// five rows of five. A vote with more options than that is five drop-down lists at most, each
/// holding 25 choices, so the most a vote can hold is Discord's own
/// <see cref="VoteLimits.MostOptions"/>.
/// </para>
/// </remarks>
public static class VoteMessage
{
    public const string Prefix = DiscordActionButton.Prefix + "vote:";

    private const string ListMark = "list";

    public static bool IsPress(string? id) => id is not null && id.StartsWith(Prefix, StringComparison.Ordinal);

    public static string ButtonId(Guid voteId, Guid optionId) => Prefix + voteId.ToString("N") + ":" + optionId.ToString("N");

    public static string ListId(Guid voteId, int index)
        => Prefix + voteId.ToString("N") + ":" + ListMark + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The vote a press is for, and the option when it was a button. False for an id that is not a
    /// vote's or is mangled; a list's id names the vote only, the option is in the press's values.
    /// </summary>
    public static bool TryRead(string? id, out Guid voteId, out Guid? optionId)
    {
        voteId = Guid.Empty;
        optionId = null;

        if (!IsPress(id))
            return false;

        var rest = id![Prefix.Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);

        if (colon < 0 || !Guid.TryParseExact(rest[..colon], "N", out voteId))
            return false;

        var after = rest[(colon + 1)..];

        if (after.StartsWith(ListMark, StringComparison.Ordinal))
            return true;

        if (!Guid.TryParseExact(after, "N", out var option))
            return false;

        optionId = option;
        return true;
    }

    private static IEnumerable<VoteOption> Ordered(Vote vote) => vote.Options.OrderBy(o => o.Position);

    /// <summary>
    /// The question, who may vote and, on a vote that shows counts, the count for each option in the
    /// buttons' order. Mentions are off, so the role is shown and not pinged. Never who voted.
    /// </summary>
    /// <param name="counts">The picks for each option; an option missing from it has none.</param>
    public static DiscordEmbedContent Open(Vote vote, IReadOnlyDictionary<Guid, int>? counts = null)
    {
        ArgumentNullException.ThrowIfNull(vote);

        var parts = new List<string>();

        if (vote.RoleId is { } role)
            parts.Add($"For <@&{role}> only");

        if (vote.ShowCounts)
        {
            parts.Add(DiscordCommandHandler.WholeLines(
                Ordered(vote).Select(o => $"**{CardText.EscapeName(o.Label)}**: "
                    + (counts?.GetValueOrDefault(o.Id) ?? 0).ToString(CultureInfo.InvariantCulture)),
                4000));
        }

        return new DiscordEmbedContent(
            CardText.Plain(vote.Question, 256),
            parts.Count == 0 ? null : string.Join("\n\n", parts),
            CardColour.Violet,
            [],
            null,
            null,
            null);
    }

    /// <summary>The question, with each option's votes, most first, once the vote is closed.</summary>
    public static DiscordEmbedContent Closed(Vote vote, IReadOnlyDictionary<Guid, int> counts)
    {
        ArgumentNullException.ThrowIfNull(vote);
        ArgumentNullException.ThrowIfNull(counts);

        var total = vote.Options.Sum(o => counts.GetValueOrDefault(o.Id));

        var lines = Ordered(vote)
            .OrderByDescending(o => counts.GetValueOrDefault(o.Id))
            .Select(o =>
            {
                var votes = counts.GetValueOrDefault(o.Id);
                var share = total == 0 ? 0 : (int)Math.Round(votes * 100.0 / total, MidpointRounding.AwayFromZero);

                return $"**{CardText.EscapeName(o.Label)}**: {votes.ToString(CultureInfo.InvariantCulture)} "
                    + $"({share.ToString(CultureInfo.InvariantCulture)}%)";
            });

        return new DiscordEmbedContent(
            CardText.Plain(vote.Question, 256),
            DiscordCommandHandler.WholeLines(lines, 4096),
            CardColour.Dark,
            [],
            null,
            null,
            total == 1 ? "Closed. 1 vote" : $"Closed. {total.ToString(CultureInfo.InvariantCulture)} votes");
    }

    /// <summary>One button for each option, when they all fit; none otherwise.</summary>
    public static IReadOnlyList<DiscordActionButton> Buttons(Vote vote)
    {
        ArgumentNullException.ThrowIfNull(vote);

        if (vote.Options.Count > VoteLimits.PerRow)
            return [];

        return [.. Ordered(vote).Select(o => new DiscordActionButton(o.Label, ButtonId(vote.Id, o.Id)))];
    }

    /// <summary>The drop-down lists, 25 choices each, when there are too many options for buttons; none otherwise.</summary>
    public static IReadOnlyList<DiscordSelectMenu> Lists(Vote vote)
    {
        ArgumentNullException.ThrowIfNull(vote);

        if (vote.Options.Count <= VoteLimits.PerRow)
            return [];

        return [.. Ordered(vote)
            .Take(VoteLimits.MostOptions)
            .Chunk(VoteLimits.PerRow)
            .Select((chunk, i) => new DiscordSelectMenu(
                ListId(vote.Id, i),
                $"Options {(i * VoteLimits.PerRow + 1).ToString(CultureInfo.InvariantCulture)} to {(i * VoteLimits.PerRow + chunk.Length).ToString(CultureInfo.InvariantCulture)}",
                [.. chunk.Select(o => new DiscordSelectChoice(o.Label, o.Id.ToString("N")))]))];
    }
}
