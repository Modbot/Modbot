using System.Globalization;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.Instances;

namespace Modbot.Discord.Calendar;

/// <summary>How a channel post or Discord event reads right now.</summary>
public enum CalendarCardState
{
    Scheduled = 1,
    Open = 2,
    Finished = 3,
    Cancelled = 4,
}

/// <summary>
/// What an event looks like in Discord: the channel post, in the style of the instance cards, and
/// the details of its server event (calendar design §3.2, §3.3).
/// </summary>
/// <remarks>
/// <para>
/// Times are Discord timestamps (<c>&lt;t:…:F&gt;</c>), so every reader sees them in their own time
/// zone and nobody has to convert.
/// </para>
/// <para>
/// The world's name is VRChat text a world's author chose, so it is escaped like a display name on an
/// instance card. The event's own title and description are the moderators' words and keep their
/// formatting; mentions are off on every message the bot sends, so neither can ping anyone.
/// </para>
/// </remarks>
public static class CalendarCard
{
    public const int DiscordEventNameLimit = 100;
    public const int DiscordEventDescriptionLimit = 1000;
    public const int DiscordEventLocationLimit = 100;

    /// <summary>Scheduled: Modbot's own violet, not Discord's.</summary>
    private const uint Violet = CardColour.Violet;
    private const uint Green = CardColour.Green;
    private const uint Dark = CardColour.Dark;
    private const uint Red = CardColour.Red;

    /// <param name="style">Where Modbot is, so the world links to its popup.</param>
    /// <param name="picture">
    /// <see cref="CardPicture.Image"/> is the event's own picture, or the world's, already sent
    /// with the message (Discord embeds design §3).
    /// </param>
    public static DiscordEmbedContent For(
        CalendarEvent calendarEvent,
        CalendarOccurrence occurrence,
        VRChatWorld? world,
        CalendarCardState state,
        string? joinLink,
        CardStyle? style = null,
        CardPicture picture = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        style ??= CardStyle.None;

        var fields = new List<DiscordEmbedField>
        {
            new("When", $"{Stamp(occurrence.StartsAt, "F")} ({Stamp(occurrence.StartsAt, "R")})", Inline: false),
            new("Ends", Stamp(occurrence.EndsAt, "t"), Inline: true),
        };

        // The world's name, opening the world's page on vrchat.com. This card is for members, who
        // cannot sign in to Modbot, so the link other cards use -- the world in Modbot -- led them
        // to a sign-in page (changed 2026-10-01, calendar design §3.3). The id stays out of the card.
        if (calendarEvent.WorldId is { Length: > 0 } worldId)
            fields.Add(new DiscordEmbedField("World", CardLink.WorldOnVRChat(world?.Name, worldId), Inline: true));
        else if (WorldName(calendarEvent, world) is { } place)
            fields.Add(new DiscordEmbedField("World", CardText.EscapeName(place), Inline: true));

        fields.Add(new DiscordEmbedField("Who can join", Access(calendarEvent.AccessType), Inline: true));
        fields.Add(new DiscordEmbedField("Region", calendarEvent.Region.ToUpperInvariant(), Inline: true));

        var link = state == CalendarCardState.Open ? joinLink : null;

        // A date with its own title or description says its own (calendar design §2.2).
        var description = CalendarRepeat.DescriptionOf(calendarEvent, occurrence);

        return new DiscordEmbedContent(
            Title: CardText.Plain(CalendarRepeat.TitleOf(calendarEvent, occurrence), 256),
            Description: string.IsNullOrWhiteSpace(description) ? null : Cut(description, 4096),
            Color: state switch
            {
                CalendarCardState.Open => Green,
                CalendarCardState.Finished => Dark,
                CalendarCardState.Cancelled => Red,
                _ => Violet,
            },
            Fields: fields,
            Timestamp: occurrence.StartsAt,
            Url: link,
            Footer: state switch
            {
                CalendarCardState.Open => "Open now",
                CalendarCardState.Finished => "Finished",
                CalendarCardState.Cancelled => "Cancelled",
                _ => "Scheduled",
            },
            ImageUrl: picture.Image,
            AuthorName: style.GroupName is { Length: > 0 } group ? CardText.Plain(group, 256) : null,
            AuthorIconUrl: picture.AuthorIcon);
    }

    /// <summary>
    /// The picture the moderators gave the event, when they gave one.
    /// </summary>
    /// <remarks>
    /// Any https address somebody typed into the calendar, so it goes into the card as it is:
    /// whatever host it is on is a host that serves it to anybody, which is the whole difference
    /// between it and a VRChat address. Only the world's picture -- <see cref="InstanceCard.PictureOf"/>
    /// -- has to be sent with the message.
    /// </remarks>
    public static string? OwnPicture(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return HttpsOnly(calendarEvent.ImageUrl);
    }

    /// <summary>The buttons under a post: Join while the instance is open, none otherwise.</summary>
    public static IReadOnlyList<DiscordLinkButton> Links(CalendarCardState state, string? joinLink) =>
        state == CalendarCardState.Open && joinLink is { Length: > 0 }
            ? [new DiscordLinkButton("Join", joinLink)]
            : [];

    /// <summary>The server event's details. <paramref name="startsAt"/> is already moved off the past.</summary>
    /// <param name="title">The date's own title, when it has one; the event's otherwise.</param>
    /// <param name="description">The date's own description, when it has one; the event's otherwise.</param>
    /// <param name="location">
    /// A short address that leads to the join link, or the join link itself, or null before the
    /// instance is open. Discord allows 100 characters here and VRChat's links are longer, so a link
    /// that does not fit goes into the description instead.
    /// </param>
    public static DiscordScheduledEventDetails EventDetails(
        CalendarEvent calendarEvent,
        VRChatWorld? world,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        string? location,
        string? joinLink,
        string? title = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        description = (description ?? calendarEvent.Description)?.Trim() ?? string.Empty;
        string where;

        if (location is { Length: > 0 and <= DiscordEventLocationLimit })
        {
            where = location;
        }
        else
        {
            where = Cut(WorldName(calendarEvent, world) ?? "VRChat", DiscordEventLocationLimit);

            if (joinLink is { Length: > 0 })
            {
                var line = "Join: " + joinLink;
                description = description.Length == 0 ? line : line + "\n\n" + description;
            }
        }

        return new DiscordScheduledEventDetails(
            Cut(title ?? calendarEvent.Title, DiscordEventNameLimit),
            description.Length == 0 ? null : Cut(description, DiscordEventDescriptionLimit),
            startsAt,
            endsAt > startsAt ? endsAt : startsAt.AddMinutes(1),
            where,
            HttpsOnly(calendarEvent.ImageUrl) ?? HttpsOnly(world?.ImageUrl));
    }

    /// <summary>
    /// The short message a cancel posts in the event's channel when the moderator ticks it: the
    /// title, the time as a Discord timestamp, and the word. A message of its own rather than only
    /// the card turning red, because an edit to an old card notifies nobody.
    /// </summary>
    /// <param name="title">One date's own title (calendar design §2.2); the event's when null.</param>
    public static string CancelNotice(CalendarEvent calendarEvent, DateTimeOffset startsAt, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        // Escaped, because it sits inside the bold: a title with a star in it would end it early.
        return $"**{CardText.EscapeName(title ?? calendarEvent.Title)}** · {Stamp(startsAt, "F")} · Cancelled";
    }

    private static string? WorldName(CalendarEvent calendarEvent, VRChatWorld? world) =>
        world?.Name is { Length: > 0 } name ? name : calendarEvent.WorldId;

    private static string? HttpsOnly(string? url) =>
        url is { Length: > 0 } && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;

    private static string Stamp(DateTimeOffset at, string style) =>
        $"<t:{at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}:{style}>";

    private static string Access(string access) => access switch
    {
        "members" => "Group members",
        "plus" => "Members and their friends",
        "public" => "Anyone",
        _ => access,
    };

    private static string Cut(string value, int limit) =>
        value.Length <= limit ? value : value[..(limit - 1)] + "…";
}
