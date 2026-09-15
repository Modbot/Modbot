using System.Globalization;
using Modbot.Core.Calendar;
using Modbot.Core.Data.Entities;
using VRChat.API.Model;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;

namespace Modbot.VRChat.Calendar;

/// <summary>
/// Turns an event into VRChat's calendar request bodies, and says when that body has changed.
/// </summary>
/// <remarks>
/// A repeating event is one VRChat series with VRChat's own recurrence, not one VRChat event per
/// occurrence (calendar design §3.1). The start sent is the occurrence Modbot is dealing with, so a
/// series whose first date has passed is not sent as starting in the past.
/// </remarks>
public static class CalendarVRChatRequests
{
    /// <summary>
    /// A hash of everything a create or update sends except the occurrence it starts from, so a
    /// repeating event moving on to its next occurrence costs no write.
    /// </summary>
    public static string Fingerprint(CalendarEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);

        return CalendarFingerprint.Of(
            e.Title, e.Description, e.StartsAt, e.EndsAt, e.TimeZone, e.Repeat, e.RepeatDays, e.RepeatUntil?.ToString("O", CultureInfo.InvariantCulture),
            e.Category, e.Languages, e.Platforms, e.Tags, e.Visibility, e.VRChatImageId, e.NotifyMembers);
    }

    public static CreateCalendarEventRequest Create(CalendarEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var (starts, ends) = Times(e);

        return new CreateCalendarEventRequest(
            accessType: e.Visibility == "public" ? CalendarEventAccess.Public : CalendarEventAccess.Group,
            category: Category(e.Category),
            description: e.Description ?? string.Empty,
            endsAt: ends,
            imageId: string.IsNullOrWhiteSpace(e.VRChatImageId) ? null! : e.VRChatImageId,
            isDraft: false,
            languages: e.Languages.Count > 0 ? [.. e.Languages] : null!,
            platforms: e.Platforms.Count > 0 ? [.. e.Platforms.Select(Platform)] : null!,
            recurrence: Recurrence(e)!,
            sendCreationNotification: e.NotifyMembers,
            startsAt: starts,
            tags: e.Tags.Count > 0 ? [.. e.Tags] : null!,
            title: e.Title);
    }

    public static UpdateCalendarEventRequest Update(CalendarEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var (starts, ends) = Times(e);

        return new UpdateCalendarEventRequest(
            // The update body takes the category as text rather than the enum; the stored word is
            // already VRChat's own.
            category: Categories.Contains(e.Category) ? e.Category : "hangout",
            description: e.Description ?? string.Empty,
            endsAt: ends,
            imageId: string.IsNullOrWhiteSpace(e.VRChatImageId) ? null! : e.VRChatImageId,
            isDraft: false,
            languages: [.. e.Languages],
            platforms: [.. e.Platforms],
            recurrence: Recurrence(e)!,
            sendCreationNotification: false,
            startsAt: starts,
            tags: [.. e.Tags],
            title: e.Title);
    }

    private static (DateTime Starts, DateTime Ends) Times(CalendarEvent e)
    {
        var length = e.EndsAt - e.StartsAt;
        var starts = e.OccurrenceStartsAt ?? e.StartsAt;
        return (starts.UtcDateTime, (starts + length).UtcDateTime);
    }

    private static CalendarEventRecurrence? Recurrence(CalendarEvent e)
    {
        var frequency = e.Repeat switch
        {
            CalendarRepeats.Daily => CalendarEventFrequency.Daily,
            CalendarRepeats.Weekly => CalendarEventFrequency.Weekly,
            CalendarRepeats.Monthly => CalendarEventFrequency.Monthly,
            _ => (CalendarEventFrequency?)null,
        };

        if (frequency is null)
            return null;

        var zone = CalendarRepeat.ZoneOf(e);

        var days = e.Repeat == CalendarRepeats.Weekly
            ? CalendarRepeat.WeeklyDays(e, zone).Select(d => Enum.Parse<CalendarDayOfWeek>(CalendarRepeat.DayName(d))).ToList()
            : null;

        // VRChat asks for the end date "without timezone or offset": the last moment of the last
        // day, as wall-clock time in the event's zone.
        var end = e.RepeatUntil is { } until
            ? new CalendarEventRecurrenceEnd(
                date: until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T23:59:59",
                type: CalendarEventRecurrenceEndType.AfterDate)
            : null;

        return new CalendarEventRecurrence(
            daysOfWeek: days!,
            end: end!,
            frequency: frequency.Value,
            interval: 1,
            timezone: zone.Id);
    }

    private static CalendarEventCategory Category(string category) => category switch
    {
        "arts" => CalendarEventCategory.Arts,
        "avatars" => CalendarEventCategory.Avatars,
        "dance" => CalendarEventCategory.Dance,
        "education" => CalendarEventCategory.Education,
        "exploration" => CalendarEventCategory.Exploration,
        "film_media" => CalendarEventCategory.FilmMedia,
        "gaming" => CalendarEventCategory.Gaming,
        "music" => CalendarEventCategory.Music,
        "other" => CalendarEventCategory.Other,
        "performance" => CalendarEventCategory.Performance,
        "roleplaying" => CalendarEventCategory.Roleplaying,
        "wellness" => CalendarEventCategory.Wellness,
        _ => CalendarEventCategory.Hangout,
    };

    private static CalendarEventPlatform Platform(string platform) => platform switch
    {
        "android" => CalendarEventPlatform.Android,
        "ios" => CalendarEventPlatform.Ios,
        _ => CalendarEventPlatform.Standalonewindows,
    };

    /// <summary>VRChat's category words, for the form and for checking a request.</summary>
    public static readonly IReadOnlyList<string> Categories =
        ["arts", "avatars", "dance", "education", "exploration", "film_media", "gaming", "hangout", "music", "other", "performance", "roleplaying", "wellness"];

    /// <summary>VRChat's platform words.</summary>
    public static readonly IReadOnlyList<string> Platforms = ["standalonewindows", "android", "ios"];
}
