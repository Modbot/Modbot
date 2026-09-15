using System.Globalization;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
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

    private const uint Blurple = 0x5865F2;
    private const uint Green = 0x3BA55D;
    private const uint Dark = 0x4F545C;
    private const uint Red = 0xED4245;

    public static DiscordEmbedContent For(
        CalendarEvent calendarEvent,
        CalendarOccurrence occurrence,
        VRChatWorld? world,
        CalendarCardState state,
        string? joinLink)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var fields = new List<DiscordEmbedField>
        {
            new("When", $"{Stamp(occurrence.StartsAt, "F")} ({Stamp(occurrence.StartsAt, "R")})", Inline: false),
            new("Ends", Stamp(occurrence.EndsAt, "t"), Inline: true),
        };

        if (WorldName(calendarEvent, world) is { } place)
            fields.Add(new DiscordEmbedField("World", InstanceCard.Escape(place), Inline: true));

        fields.Add(new DiscordEmbedField("Who can join", Access(calendarEvent.AccessType), Inline: true));
        fields.Add(new DiscordEmbedField("Region", calendarEvent.Region.ToUpperInvariant(), Inline: true));

        var link = state == CalendarCardState.Open ? joinLink : null;

        return new DiscordEmbedContent(
            Title: Cut(calendarEvent.Title, 256),
            Description: string.IsNullOrWhiteSpace(calendarEvent.Description) ? null : Cut(calendarEvent.Description, 4096),
            Color: state switch
            {
                CalendarCardState.Open => Green,
                CalendarCardState.Finished => Dark,
                CalendarCardState.Cancelled => Red,
                _ => Blurple,
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
            ImageUrl: Picture(calendarEvent, world));
    }

    /// <summary>The buttons under a post: Join while the instance is open, none otherwise.</summary>
    public static IReadOnlyList<DiscordLinkButton> Links(CalendarCardState state, string? joinLink) =>
        state == CalendarCardState.Open && joinLink is { Length: > 0 }
            ? [new DiscordLinkButton("Join", joinLink)]
            : [];

    /// <summary>The server event's details. <paramref name="startsAt"/> is already moved off the past.</summary>
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
        string? joinLink)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var description = calendarEvent.Description?.Trim() ?? string.Empty;
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
            Cut(calendarEvent.Title, DiscordEventNameLimit),
            description.Length == 0 ? null : Cut(description, DiscordEventDescriptionLimit),
            startsAt,
            endsAt > startsAt ? endsAt : startsAt.AddMinutes(1),
            where,
            HttpsOnly(calendarEvent.ImageUrl) ?? HttpsOnly(world?.ImageUrl));
    }

    private static string? WorldName(CalendarEvent calendarEvent, VRChatWorld? world) =>
        world?.Name is { Length: > 0 } name ? name : calendarEvent.WorldId;

    private static string? Picture(CalendarEvent calendarEvent, VRChatWorld? world) =>
        HttpsOnly(calendarEvent.ImageUrl) ?? world?.ImageUrl ?? world?.ThumbnailImageUrl;

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
