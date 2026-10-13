using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Google;

namespace Modbot.Core.Calendar;

/// <summary>
/// The addresses an event's texts can name without anyone typing them (calendar templates plan,
/// step 4): this event on Google, this event on Discord, the server's public Google calendar to add,
/// and the VRChat group's page. A null address is one that does not exist (yet).
/// </summary>
/// <param name="GoogleEvent">The event on the Google calendar: known once Google has answered the first insert.</param>
/// <param name="DiscordEvent">The event's Discord scheduled event: the current date's once Discord has made it, the last one's until then.</param>
/// <param name="AddGoogleCalendar">Google's "add this calendar" page for the server's public calendar.</param>
/// <param name="VRChatGroupPage">The group's page on vrchat.com.</param>
public sealed record CalendarLinks(string? GoogleEvent, string? DiscordEvent, string? AddGoogleCalendar, string? VRChatGroupPage)
{
    /// <summary>No address known: every link is left out.</summary>
    public static readonly CalendarLinks None = new(null, null, null, null);
}

/// <summary>
/// Automatic links in an event's or template's texts, and how they are filled in when the texts are
/// sent (calendar templates plan, step 4).
/// </summary>
/// <remarks>
/// <para>
/// A link is stored the way a fill-in is, its name between double curly brackets (<c>{{Google event}}</c>),
/// and the four names below mean something: they are never asked for when a template is used, and
/// the page draws them as boxes. Names are matched without regard to capitals or extra spaces.
/// </para>
/// <para>
/// <strong>Left out cleanly.</strong> A link whose address does not exist (no public Google calendar
/// saved, no VRChat group, an event not yet on Google or Discord) is removed along with the spaces
/// before it, and a line left with nothing else on it goes too, so no text is left half-said. The
/// event's texts are sent again once the address is known, by the same fingerprint check an edit
/// goes through; nothing is sent for it that the event's publish did not already send.
/// </para>
/// </remarks>
public static partial class CalendarAutoLinks
{
    public const string GoogleEventName = "Google event";
    public const string DiscordEventName = "Discord event";
    public const string AddGoogleCalendarName = "Add our Google calendar";
    public const string VRChatGroupPageName = "VRChat group page";

    /// <summary>The names that mean a link, in the order the page offers them.</summary>
    public static readonly IReadOnlyList<string> Names =
        [GoogleEventName, DiscordEventName, AddGoogleCalendarName, VRChatGroupPageName];

    /// <summary>What a name is compared by: trimmed, single spaces, lower case.</summary>
    public static string Key(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Spaces().Replace(name.Trim(), " ").ToLowerInvariant();
    }

    /// <summary>Whether <paramref name="name"/> names one of the four links.</summary>
    public static bool IsLink(string name) => Names.Any(n => Key(n) == Key(name));

    /// <summary>The links that exist for an event right now.</summary>
    /// <param name="calendarEvent">The event whose places are read.</param>
    /// <param name="settings">The server's settings: the Discord server, the Google calendar, the VRChat group.</param>
    /// <param name="places">The event's places, of which the Google and Discord event ones are read.</param>
    public static CalendarLinks For(CalendarEvent calendarEvent, Settings settings, IEnumerable<CalendarEventPlace> places)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(places);

        string? google = null;
        string? discord = null;

        foreach (var place in places)
        {
            if (place.EventId != calendarEvent.Id || place.State == CalendarPlaceStates.Removed)
                continue;

            if (place.Place == CalendarPlaces.Google)
                google = string.IsNullOrWhiteSpace(place.GoogleLink) ? null : place.GoogleLink;

            // The Discord event the event has right now, whichever date it was made for: when the date
            // moves on, Discord ends that one and makes the next in a single pass, and until then the
            // other sites keep the address they have, so each site is updated once per date -- with the
            // new address -- and never once without it and again with it.
            if (place.Place == CalendarPlaces.DiscordEvent
                && place.ExternalId is { Length: > 0 } id
                && settings.DiscordGuildId?.Trim() is { Length: > 0 } guild)
            {
                discord = $"https://discord.com/events/{Uri.EscapeDataString(guild)}/{Uri.EscapeDataString(id)}";
            }
        }

        var add = GoogleCalendarIds.IsUsable(settings.GoogleCalendarId) && settings.GooglePublic == "all"
            ? GoogleCalendarIds.SubscribeLink(settings.GoogleCalendarId!)
            : null;

        var group = settings.ManagedGroupId is { Length: > 0 } groupId
            ? $"https://vrchat.com/home/group/{Uri.EscapeDataString(groupId)}"
            : null;

        return new CalendarLinks(google, discord, add, group);
    }

    /// <summary>
    /// Sets <see cref="CalendarEvent.AutoLinks"/> on each event, from its places: the one extra read
    /// a pass that sends texts makes.
    /// </summary>
    public static async Task ResolveAsync(
        ModbotContext db, Settings settings, IReadOnlyCollection<CalendarEvent> events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
            return;

        var ids = events.Select(e => e.Id).ToList();

        var places = await db.CalendarEventPlaces.AsNoTracking()
            .Where(p => ids.Contains(p.EventId)
                && (p.Place == CalendarPlaces.Google || p.Place == CalendarPlaces.DiscordEvent)
                && p.State != CalendarPlaceStates.Removed)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var calendarEvent in events)
            calendarEvent.AutoLinks = For(calendarEvent, settings, places);
    }

    /// <summary>
    /// <paramref name="text"/> with each link replaced by its address, or left out when it has none.
    /// A name that is not one of the four stays as it is (a fill-in nobody filled in is not Modbot's to guess).
    /// </summary>
    public static string Fill(string text, CalendarLinks? links)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!text.Contains("{{", StringComparison.Ordinal))
            return text;

        var kept = new List<string>();
        var droppedLast = false;

        foreach (var line in text.Split('\n'))
        {
            var removed = false;

            var filled = Marker().Replace(line, match =>
            {
                var name = match.Groups["name"].Value;

                if (!IsLink(name))
                    return match.Value;

                if (AddressOf(name, links) is { } address)
                    return match.Groups["lead"].Value + address;

                removed = true;
                return string.Empty;
            });

            if (removed)
            {
                filled = filled.TrimEnd();

                if (filled.Length == 0)
                {
                    droppedLast = true;
                    continue;
                }
            }
            else if (droppedLast && string.IsNullOrWhiteSpace(line) && (kept.Count == 0 || string.IsNullOrWhiteSpace(kept[^1])))
            {
                // The paragraph break that stood after a dropped line is not needed twice.
                droppedLast = false;
                continue;
            }

            droppedLast = false;
            kept.Add(filled);
        }

        return string.Join('\n', kept);
    }

    /// <summary>
    /// The event's links as <paramref name="site"/> is sent them (null: Modbot's own pages and feed), for
    /// the event as a whole or for one date held apart (<paramref name="ofADate"/>). Discord's own texts leave the Discord event out
    /// (it would link to itself, and a new date's event is made before its address exists). A date's
    /// own texts leave it out too: they are sent to a site once and again only when they are edited,
    /// so a Discord address in them would be wrong from the next date on, and putting it in would
    /// send every such date again each time the date moves on.
    /// </summary>
    public static CalendarLinks? For(CalendarEvent calendarEvent, CalendarSite? site, bool ofADate = false)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return calendarEvent.AutoLinks is { } links && (site == CalendarSite.Discord || ofADate)
            ? links with { DiscordEvent = null }
            : calendarEvent.AutoLinks;
    }

    /// <summary>Whether <paramref name="text"/> holds one of the four links. A title may not (links go in descriptions).</summary>
    public static bool HasLink(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
            return false;

        return Marker().Matches(text).Any(m => IsLink(m.Groups["name"].Value));
    }

    private static string? AddressOf(string name, CalendarLinks? links)
    {
        if (links is null)
            return null;

        var key = Key(name);
        var address =
            key == Key(GoogleEventName) ? links.GoogleEvent
            : key == Key(DiscordEventName) ? links.DiscordEvent
            : key == Key(AddGoogleCalendarName) ? links.AddGoogleCalendar
            : links.VRChatGroupPage;

        return string.IsNullOrWhiteSpace(address) ? null : address;
    }

    [GeneratedRegex(@"(?<lead>[ \t]*)\{\{(?<name>[^{}\r\n]{1,80})\}\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Marker();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Spaces();
}
