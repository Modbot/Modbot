using System.Globalization;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Instances;

/// <summary>
/// What a room looks like in Discord: one card, rewritten as the room fills and empties, and
/// written one last time when it closes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a notice board for members, not a record for moderators.</strong> The
/// moderation log says what the team did; this says "we are in here, come along". So it carries
/// the world, how many people are in, and how long it has been running -- and it carries nobody's
/// name. Who is in an instance is exactly the sort of thing a member has not agreed to have
/// posted in a public channel, and a room's population is a number here rather than a roster for
/// that reason.
/// </para>
/// <para>
/// The card never links straight into the instance. A group room's location string carries the
/// qualifiers that let anybody holding it join, and a channel is not the place to publish one.
/// </para>
/// </remarks>
public static class InstanceCard
{
    /// <summary>Open, and somebody is in it.</summary>
    private const uint Green = 0x3BA55D;

    /// <summary>Open, and empty.</summary>
    private const uint Grey = 0x747F8D;

    /// <summary>Finished.</summary>
    private const uint Dark = 0x4F545C;

    /// <summary>
    /// Builds the card for a room as it stands right now.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <param name="world">Its world, when the name is known. Null falls back to the world id.</param>
    /// <param name="now">The moment the card is being written, for "open for 2h 14m".</param>
    public static DiscordEmbedContent For(VRChatInstance room, VRChatWorld? world, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(room);

        var closed = room.ClosedAt is not null;
        var people = room.LastUserCount ?? 0;

        var fields = new List<DiscordEmbedField>();

        // The live number, and the word that goes with it. "1 person", not "1 people".
        fields.Add(new DiscordEmbedField(
            closed ? "People at the end" : "People here now",
            people == 1 ? "1 person" : $"{people} people",
            Inline: true));

        if (room.PeakUserCount is { } peak && peak > people)
            fields.Add(new DiscordEmbedField("Most at once", peak.ToString(CultureInfo.InvariantCulture), Inline: true));

        fields.Add(new DiscordEmbedField(
            closed ? "Ran for" : "Open for",
            Duration((closed ? room.ClosedAt!.Value : now) - room.OpenedAt),
            Inline: true));

        if (room.GroupAccessType is { Length: > 0 } access)
            fields.Add(new DiscordEmbedField("Who can join", Access(access), Inline: true));

        if (room.Region is { Length: > 0 } region)
            fields.Add(new DiscordEmbedField("Region", region.ToUpperInvariant(), Inline: true));

        // The instance number, so a moderator reading the channel can match it to what they see
        // in game. Not the whole location string -- see the remarks above.
        if (room.VRChatInstanceId is { Length: > 0 } number)
            fields.Add(new DiscordEmbedField("Instance", number, Inline: true));

        var colour = closed ? Dark : people > 0 ? Green : Grey;

        return new DiscordEmbedContent(
            Title: world?.Name is { Length: > 0 } name ? name : room.WorldId,
            Description: closed ? "This instance has closed." : null,
            Color: colour,
            Fields: fields,
            Timestamp: closed ? room.ClosedAt : room.OpenedAt,
            Url: null,
            Footer: closed ? "Closed" : "Open now");
    }

    /// <summary>
    /// Turns VRChat's access word into one a member would use.
    /// </summary>
    /// <remarks>
    /// A word this build has not seen is passed through rather than replaced with "unknown": it is
    /// still VRChat's own answer, and showing it is more use than hiding it.
    /// </remarks>
    private static string Access(string groupAccessType) => groupAccessType switch
    {
        "members" => "Group members",
        "plus" => "Members and their friends",
        "public" => "Anyone",
        _ => groupAccessType,
    };

    /// <summary>
    /// "2h 14m", "8m", "3d 4h" -- long enough to be useful, short enough to sit on one line.
    /// </summary>
    /// <remarks>
    /// Seconds are never shown. A room that has been open for eleven seconds is new, and a card
    /// that ticks every second would be a card nobody could read.
    /// </remarks>
    private static string Duration(TimeSpan open)
    {
        if (open < TimeSpan.Zero)
            open = TimeSpan.Zero;

        if (open.TotalMinutes < 1)
            return "just now";

        if (open.TotalHours < 1)
            return $"{(int)open.TotalMinutes}m";

        if (open.TotalDays < 1)
            return $"{(int)open.TotalHours}h {open.Minutes}m";

        return $"{(int)open.TotalDays}d {open.Hours}h";
    }
}
