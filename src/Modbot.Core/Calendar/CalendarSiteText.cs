using Modbot.Core.Data.Entities;

namespace Modbot.Core.Calendar;

/// <summary>The sites an event is published to, each of which may say its own title and description.</summary>
public enum CalendarSite
{
    VRChat,
    Discord,
    Google,
}

/// <summary>
/// The title and description one site shows for an event: the site's own when it was given one,
/// the shared one otherwise.
/// </summary>
/// <remarks>
/// <para>
/// A date changed on its own (<see cref="CalendarDateChange"/>) keeps winning: its title or
/// description, when it has one, is said on every site, replacing the site's own for that date.
/// A date's words are typed once, on the date, so they are the one thing every site agrees on for
/// it; a site's own words are the event's, and the date has stepped away from the event.
/// </para>
/// <para>
/// Modbot's own pages, the calendar feed, reminders and invites say the shared title.
/// </para>
/// </remarks>
public static class CalendarSiteText
{
    /// <summary>The title <paramref name="site"/> shows for the event as a whole.</summary>
    public static string Title(CalendarEvent calendarEvent, CalendarSite site)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return Own(OwnTitle(calendarEvent, site)) ?? calendarEvent.Title;
    }

    /// <summary>The description <paramref name="site"/> shows for the event as a whole.</summary>
    public static string Description(CalendarEvent calendarEvent, CalendarSite site)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return Own(OwnDescription(calendarEvent, site)) ?? calendarEvent.Description;
    }

    /// <summary>The site's own title, or null when it says the shared one.</summary>
    public static string? OwnTitle(CalendarEvent calendarEvent, CalendarSite site)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return Own(site switch
        {
            CalendarSite.VRChat => calendarEvent.VRChatTitle,
            CalendarSite.Discord => calendarEvent.DiscordTitle,
            _ => calendarEvent.GoogleTitle,
        });
    }

    /// <summary>The site's own description, or null when it says the shared one.</summary>
    public static string? OwnDescription(CalendarEvent calendarEvent, CalendarSite site)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return Own(site switch
        {
            CalendarSite.VRChat => calendarEvent.VRChatDescription,
            CalendarSite.Discord => calendarEvent.DiscordDescription,
            _ => calendarEvent.GoogleDescription,
        });
    }

    private static string? Own(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
