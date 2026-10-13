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
/// Modbot's own pages, reminders and invites say the shared title.
/// </para>
/// <para>
/// The words are returned with the event's automatic links filled in (<see cref="CalendarAutoLinks"/>),
/// from <see cref="CalendarEvent.AutoLinks"/>: what a site is sent, and so what its fingerprint is of.
/// <see cref="OwnTitle"/> and <see cref="OwnDescription"/> are the words as typed.
/// </para>
/// </remarks>
public static class CalendarSiteText
{
    /// <summary>The title <paramref name="site"/> shows for the event as a whole.</summary>
    public static string Title(CalendarEvent calendarEvent, CalendarSite site)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return CalendarAutoLinks.Fill(Own(OwnTitle(calendarEvent, site)) ?? calendarEvent.Title, calendarEvent.AutoLinks);
    }

    /// <summary>The description <paramref name="site"/> shows for the event as a whole.</summary>
    public static string Description(CalendarEvent calendarEvent, CalendarSite site)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return CalendarAutoLinks.Fill(Own(OwnDescription(calendarEvent, site)) ?? calendarEvent.Description, calendarEvent.AutoLinks);
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
