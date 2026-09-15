using System.Globalization;
using System.Text;
using Modbot.Core.Data;
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
/// the world, how many people are in, and how long it has been running.
/// </para>
/// <para>
/// <strong>Names show while a moderator is watching, unless turned off.</strong> While somebody
/// from the team is in the room, the card lists the display names of the people there -- up to
/// <see cref="NamesListed"/>, then "and N more" -- so a member can see who they would be joining.
/// When nobody is watching the card shows the head count only, because Modbot does not know who is
/// inside. The operator can turn names off (<c>Settings.DiscordInstanceShowNames</c>, on by default)
/// for a community that would rather not have them posted.
/// </para>
/// <para>
/// <strong>Display names are hostile input.</strong> They are chosen by anybody, so every Discord
/// markdown character in one is escaped before it reaches a card, and the list is cut to fit
/// Discord's field limit rather than letting a long name push the card over it. Mentions are
/// disabled on every message this bot sends, so a name spelled like a mention is text.
/// </para>
/// <para>
/// <strong>The card links straight into the instance</strong>, through the world name and a Join
/// button, both opening VRChat's launch page (<see cref="JoinLink"/>). It is only ever posted for
/// the group's own rooms, and VRChat still applies the room's access -- group members, members and
/// their friends, or anyone -- when the link is opened, so it lets the people who could already
/// join do it in one click and lets nobody else in. Both go away when the room closes.
/// </para>
/// <para>
/// <strong>The world's picture</strong> is the one VRChat's world page gave, as stored in
/// <c>vrchat_world</c>. A world not read yet has none, and the card goes without it.
/// </para>
/// </remarks>
public static class InstanceCard
{
    /// <summary>How many names a card lists before it says "and N more".</summary>
    public const int NamesListed = 20;

    /// <summary>Discord's limit on one embed field's value, in characters.</summary>
    public const int FieldValueLimit = 1024;

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
    /// <param name="names">
    /// The display names of the people here while a moderator is watching, and names are turned on.
    /// Null when nobody is watching or names are off, and then no names are shown. A null entry is a
    /// person whose name is not known; they are counted in "and N more", never shown by id.
    /// </param>
    public static DiscordEmbedContent For(
        VRChatInstance room,
        VRChatWorld? world,
        DateTimeOffset now,
        IReadOnlyList<string?>? names = null)
    {
        ArgumentNullException.ThrowIfNull(room);

        var closed = room.ClosedAt is not null;
        var people = HeadCounts.Shown(room) ?? 0;

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
            fields.Add(new DiscordEmbedField("Region", Fit(Escape(region.ToUpperInvariant())), Inline: true));

        // What the world's page says it holds, when it has been read. Shown, never enforced.
        if (world?.Capacity is > 0 and var capacity)
            fields.Add(new DiscordEmbedField("Capacity", capacity.ToString(CultureInfo.InvariantCulture), Inline: true));

        // The instance number, so a moderator reading the channel can match it to what they see
        // in game. The full location goes in the join link instead of on screen.
        //
        // Escaped like a display name, because it is not always a number: a group can set an
        // instance id to any text through the API (M6 spec 4.1.1), and it would otherwise render as
        // formatting on a card every member reads.
        if (room.VRChatInstanceId is { Length: > 0 } number)
            fields.Add(new DiscordEmbedField("Instance", Fit(Escape(number)), Inline: true));

        if (!closed && names is { Count: > 0 } && NameList(names) is { } list)
            fields.Add(new DiscordEmbedField("Who is here", list, Inline: false));

        var colour = closed ? Dark : people > 0 ? Green : Grey;

        return new DiscordEmbedContent(
            Title: world?.Name is { Length: > 0 } name ? name : room.WorldId,
            Description: closed ? "This instance has closed." : null,
            Color: colour,
            Fields: fields,
            Timestamp: closed ? room.ClosedAt : room.OpenedAt,
            Url: closed ? null : JoinLink(room),
            Footer: closed ? "Closed" : "Open now",
            ImageUrl: world?.ImageUrl ?? world?.ThumbnailImageUrl);
    }

    /// <summary>
    /// The buttons under a room's card: Join while it is open, none once it has closed.
    /// </summary>
    public static IReadOnlyList<DiscordLinkButton> Links(VRChatInstance room)
    {
        ArgumentNullException.ThrowIfNull(room);

        return room.ClosedAt is null && JoinLink(room) is { } link
            ? [new DiscordLinkButton("Join", link)]
            : [];
    }

    /// <summary>
    /// VRChat's launch page for a room:
    /// <c>https://vrchat.com/home/launch?worldId=wrld_…&amp;instanceId=26093~group(grp_…)~groupAccessType(plus)~region(us)</c>.
    /// </summary>
    /// <remarks>
    /// <c>instanceId</c> is everything after the first <c>:</c> of the location, qualifiers and all,
    /// because the qualifiers are part of which room it is. The characters VRChat writes in a
    /// location (<c>~</c>, <c>(</c>, <c>)</c>) are kept as they are, and anything that could end or
    /// split the query string is escaped.
    /// </remarks>
    /// <returns>
    /// Null when the location has no instance part, or when the link would be longer than
    /// <see cref="MaxLinkLength"/>.
    /// </returns>
    public static string? JoinLink(VRChatInstance room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return InstanceJoinLink.For(room.Location, room.WorldId);
    }

    /// <summary>The longest address Discord accepts for a link button.</summary>
    public const int MaxLinkLength = InstanceJoinLink.MaxLength;

    /// <summary>Cuts a value to fit one embed field.</summary>
    private static string Fit(string value) =>
        value.Length <= FieldValueLimit ? value : value[..(FieldValueLimit - 1)] + "…";

    /// <summary>
    /// The names as one field value: escaped, one per line, at most <see cref="NamesListed"/>, then
    /// "and N more", and never longer than <see cref="FieldValueLimit"/>.
    /// </summary>
    /// <returns>Null when there is nothing to show.</returns>
    public static string? NameList(IReadOnlyList<string?> names)
    {
        var shown = names
            .Select(n => n is null ? null : Escape(n))
            .Where(n => n is { Length: > 0 })
            .Select(n => n!)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var listed = new List<string>();
        var length = 0;

        foreach (var name in shown.Take(NamesListed))
        {
            // Room is kept for the longest "and N more" line this list could end with, so adding a
            // name can never push the finished value past the limit.
            var more = Suffix(names.Count - listed.Count - 1);
            var next = length + (listed.Count > 0 ? 1 : 0) + name.Length;

            if (next + (more.Length > 0 ? 1 + more.Length : 0) > FieldValueLimit)
                break;

            listed.Add(name);
            length = next;
        }

        if (listed.Count == 0)
            return null;

        var value = new StringBuilder(string.Join('\n', listed));
        var remaining = names.Count - listed.Count;

        if (remaining > 0)
            value.Append('\n').Append(Suffix(remaining));

        return value.ToString();
    }

    private static string Suffix(int remaining) =>
        remaining <= 0 ? string.Empty : $"and {remaining.ToString(CultureInfo.InvariantCulture)} more";

    /// <summary>
    /// Makes a display name inert: every character Discord reads as formatting is escaped, and line
    /// breaks and other control characters become spaces so one name cannot start a heading, a
    /// quote or a list on a line of its own.
    /// </summary>
    public static string Escape(string displayName)
    {
        var escaped = new StringBuilder(displayName.Length + 8);

        foreach (var c in displayName)
        {
            if (char.IsControl(c))
            {
                escaped.Append(' ');
                continue;
            }

            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '<' or '#' or '-' or '[' or ']' or '(' or ')' or ':' or '@')
                escaped.Append('\\');

            escaped.Append(c);
        }

        return escaped.ToString().Trim();
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
